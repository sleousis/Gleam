using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

namespace TidyUp.Core.Planning;

/// <summary>Inputs the planner needs beyond the scanned items themselves.</summary>
public sealed class PlannerInputs
{
    public required ItemContext Context { get; init; }
    public required Profile Profile { get; init; }
    public required Func<uint, ItemInfo?> InfoLookup { get; init; }
    public required ItemList ProtectList { get; init; }
    public required ItemList AlwaysDiscardList { get; init; }

    /// <summary>Row keys the user unchecked or skipped earlier this session.</summary>
    public IReadOnlySet<string> SessionSkips { get; init; } = new HashSet<string>();

    /// <summary>Whether each container/owner is currently reachable by the executors.</summary>
    public required Func<ContainerKind, ulong, bool> IsAvailable { get; init; }

    /// <summary>Retainer id → display name.</summary>
    public IReadOnlyDictionary<ulong, string> RetainerNames { get; init; } = new Dictionary<ulong, string>();

    /// <summary>
    /// Also list every item no rule proposed, unchecked, so the user can pick by hand. Hard-blocked and
    /// protected items stay out; everything else gets a row with the sensible default action.
    /// </summary>
    public bool IncludeUnproposed { get; init; }
}

/// <summary>Turns scanned items into the confirmation window's content. Pure: no game access.</summary>
public sealed class RunPlanner
{
    private readonly RuleEngine engine;

    public RunPlanner(RuleEngine? engine = null)
    {
        this.engine = engine ?? new RuleEngine();
    }

    public RunPlan Build(IReadOnlyList<ScannedItem> items, PlannerInputs inputs)
    {
        var ctx = inputs.Context;
        var profile = inputs.Profile;
        var plan = new RunPlan { CharacterId = ctx.CharacterId, CharacterName = ctx.CharacterName };

        var candidates = new List<ScannedItem>();
        var userForced = new List<(ScannedItem Item, ItemInfo Info)>();
        // Discardable, but no rule may ever propose them (Fantasia, ultimate weapons, minions, crystals...).
        var guarded = new List<(ScannedItem Item, ItemInfo Info, string Why)>();

        foreach (var item in items)
        {
            if (!profile.IsContainerEnabled(item.Slot.Kind)) continue;
            if (item.Slot.Kind == ContainerKind.Retainer && profile.ExcludedRetainerIds.Contains(item.Slot.OwnerId)) continue;

            var info = inputs.InfoLookup(item.ItemId);
            if (info is null) continue;

            // 1. Hard blocks: immovable ones vanish; the rest beat the blacklist and every rule, but stay
            //    visible as hand-pick rows so the list really is everything that can be discarded.
            var block = HardBlocks.Check(item, info, ctx);
            if (block != HardBlockReason.None && HardBlocks.IsImmovable(block))
            {
                plan.Excluded.Add(new ExcludedItem(item, info, HardBlocks.Describe(block), true));
                continue;
            }

            // 2. Protect list: checked before any rule runs.
            if (inputs.ProtectList.Contains(item.ItemId, item.IsHq, ctx.CharacterId))
            {
                plan.Excluded.Add(new ExcludedItem(item, info, "On your Never touch list", false));
                continue;
            }

            if (block != HardBlockReason.None)
            {
                if (inputs.IncludeUnproposed) guarded.Add((item, info, HardBlocks.Describe(block)));
                else plan.Excluded.Add(new ExcludedItem(item, info, HardBlocks.Describe(block), true));
                continue;
            }

            // 3. Always-discard list injects a user-confidence proposal and skips the rules.
            if (inputs.AlwaysDiscardList.Contains(item.ItemId, item.IsHq, ctx.CharacterId))
            {
                userForced.Add((item, info));
                continue;
            }

            candidates.Add(item);
        }

        var proposals = new List<Proposal>(engine.Evaluate(candidates, inputs.InfoLookup, ctx, profile.Thresholds, profile.EnabledRules));

        if (inputs.IncludeUnproposed)
        {
            var proposedSlots = new HashSet<SlotRef>(proposals.Select(p => p.Item.Slot));
            var handPick = candidates
                .Where(c => !proposedSlots.Contains(c.Slot))
                .Select(c => (Item: c, Info: inputs.InfoLookup(c.ItemId), Why: (string?)null))
                .Concat(guarded.Select(g => (g.Item, Info: (ItemInfo?)g.Info, Why: (string?)g.Why)));
            foreach (var (item, info, why) in handPick)
            {
                if (info is null) continue;
                var canSell = !info.IsUntradable && info.VendorPrice > 0 && ContainerConstraints.AllowsAction(item.Slot.Kind, ActionKind.VendorSell);
                var value = canSell ? (long)info.VendorPrice * item.Quantity : 0;
                var warnings = new List<string>();
                if (why is not null) warnings.Add(why);
                else warnings.Add("Not suggested by any rule");
                if (info.IsUsable) warnings.Add("Usable item");
                if (info.IsUntradable && why is null) warnings.Add("Untradeable");
                proposals.Add(new Proposal
                {
                    Item = item, Info = info,
                    Action = canSell ? ActionKind.VendorSell : ActionKind.Discard,
                    Alternatives = canSell ? [ActionKind.Discard] : [],
                    Confidence = Confidence.Low,
                    RuleId = "manual",
                    Reason = "Picked by hand",
                    ValueGil = value,
                    ValueLabel = value > 0 ? $"{value:N0}g" : "—",
                    Warnings = warnings,
                });
            }
        }

        foreach (var (item, info) in userForced)
        {
            var vendorTotal = (long)info.VendorPrice * item.Quantity;
            proposals.Add(new Proposal
            {
                Item = item, Info = info,
                Action = info.VendorPrice > 0 ? ActionKind.VendorSell : ActionKind.Discard,
                Alternatives = info.VendorPrice > 0 ? [ActionKind.Discard] : [],
                Confidence = Confidence.User,
                RuleId = "always-discard",
                Reason = "On your Always clean list",
                ValueGil = vendorTotal,
                ValueLabel = vendorTotal > 0 ? $"{vendorTotal:N0}g" : "—",
                Warnings = item.HasMateria ? [$"Retrieve materia first ({item.MateriaCount} slotted)"] : [],
            });
        }

        foreach (var raw in proposals.OrderBy(p => p.Item.Slot.Kind.ExecutionOrder()).ThenBy(p => p.Item.Slot.OwnerId))
        {
            // Every row carries the lowest market price for its quality so the window can show it and the
            // market-board policy can price listings.
            var p = raw;
            if (p.Info.IsMarketable && ctx.MarketPrices.TryGetValue(p.Info.ItemId, out var mp))
                p = p with { MarketUnitPrice = mp.MinFor(p.Item.IsHq) };
            if (ctx.Registered.TryGetValue(p.Info.ItemId, out var registered))
                p = p with { Registered = registered };

            // Preset policy first (the headline promise), then the user's finer per-rule override, then physics.
            // Hand-picked rows follow the preset too (Discard all means discard everywhere), but are never dropped:
            // the user asked to see them.
            var constrained = ContainerConstraints.Apply(p);
            var policed = ActionPolicyApplier.Apply(constrained, profile.Thresholds.Policy);
            if (policed is null)
            {
                if (p.RuleId == "manual") policed = constrained;
                else
                {
                    plan.Excluded.Add(new ExcludedItem(p.Item, p.Info, ActionPolicyApplier.DropReason(profile.Thresholds.Policy), false));
                    continue;
                }
            }
            var proposal = ContainerConstraints.Apply(ApplyActionOverride(policed, profile));
            var section = GetSection(plan, proposal.Item, inputs);
            var row = new PlanRow { Proposal = proposal, ChosenAction = proposal.Action };
            row.Checked = proposal.DefaultChecked && !inputs.SessionSkips.Contains(row.Key);
            section.Rows.Add(row);
        }

        foreach (var s in plan.Sections)
            s.Rows.Sort((a, b) => string.Compare(a.Info.Name, b.Info.Name, StringComparison.OrdinalIgnoreCase));

        return plan;
    }

    private static Proposal ApplyActionOverride(Proposal p, Profile profile)
    {
        if (!p.Action.IsDestructive()) return p;
        if (!profile.RuleActionOverrides.TryGetValue(p.RuleId, out var preferred)) return p;
        if (preferred == p.Action || !preferred.IsDestructive()) return p;
        if (!p.Alternatives.Contains(preferred) || !ContainerConstraints.AllowsAction(p.Item.Slot.Kind, preferred)) return p;
        var alts = new List<ActionKind> { p.Action };
        alts.AddRange(p.Alternatives.Where(a => a != preferred));
        return p with { Action = preferred, Alternatives = alts };
    }

    private static PlanSection GetSection(RunPlan plan, ScannedItem item, PlannerInputs inputs)
    {
        var kind = item.Slot.Kind;
        var owner = kind == ContainerKind.Retainer ? item.Slot.OwnerId : 0;
        var section = plan.Sections.FirstOrDefault(s => s.Kind == kind && s.OwnerId == owner);
        if (section is not null) return section;

        var name = kind == ContainerKind.Retainer
            ? (inputs.RetainerNames.TryGetValue(owner, out var n) ? n : item.OwnerName)
            : string.Empty;
        section = new PlanSection
        {
            Kind = kind, OwnerId = owner, OwnerName = name,
            IsAvailableNow = inputs.IsAvailable(kind, owner),
            Requirement = kind.RequirementText(),
        };
        plan.Sections.Add(section);
        return section;
    }
}
