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

        foreach (var item in items)
        {
            if (!profile.IsContainerEnabled(item.Slot.Kind)) continue;
            if (item.Slot.Kind == ContainerKind.Retainer && profile.ExcludedRetainerIds.Contains(item.Slot.OwnerId)) continue;

            var info = inputs.InfoLookup(item.ItemId);
            if (info is null) continue;

            // 1. Hard blocks: immovable, and they beat the blacklist.
            var block = HardBlocks.Check(item, info, ctx);
            if (block != HardBlockReason.None)
            {
                plan.Excluded.Add(new ExcludedItem(item, info, HardBlocks.Describe(block), true));
                continue;
            }

            // 2. Protect list: checked before any rule runs.
            if (inputs.ProtectList.Contains(item.ItemId, item.IsHq, ctx.CharacterId))
            {
                plan.Excluded.Add(new ExcludedItem(item, info, "On your protect list", false));
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
            foreach (var item in candidates)
            {
                if (proposedSlots.Contains(item.Slot)) continue;
                var info = inputs.InfoLookup(item.ItemId);
                if (info is null) continue;
                var canSell = !info.IsUntradable && info.VendorPrice > 0 && ContainerConstraints.AllowsAction(item.Slot.Kind, ActionKind.VendorSell);
                var value = canSell ? (long)info.VendorPrice * item.Quantity : 0;
                var warnings = new List<string> { "Not proposed by any rule" };
                if (info.IsUsable) warnings.Add("Usable item");
                if (info.IsUntradable) warnings.Add("Untradeable");
                proposals.Add(new Proposal
                {
                    Item = item, Info = info,
                    Action = canSell ? ActionKind.VendorSell : ActionKind.Discard,
                    Alternatives = canSell ? [ActionKind.Discard] : [],
                    Confidence = Confidence.Low,
                    RuleId = "manual",
                    Reason = "Hand-picked",
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
                Reason = "On your always-discard list",
                ValueGil = vendorTotal,
                ValueLabel = vendorTotal > 0 ? $"{vendorTotal:N0}g" : "—",
                Warnings = item.HasMateria ? [$"Retrieve materia first ({item.MateriaCount} slotted)"] : [],
            });
        }

        foreach (var p in proposals.OrderBy(p => p.Item.Slot.Kind.ExecutionOrder()).ThenBy(p => p.Item.Slot.OwnerId))
        {
            // Preset policy first (the headline promise), then the user's finer per-rule override, then physics.
            // Hand-picked rows already carry the only sensible default and are never dropped by a policy.
            var policed = p.RuleId == "manual" ? p : ActionPolicyApplier.Apply(ContainerConstraints.Apply(p), profile.Thresholds.Policy);
            if (policed is null)
            {
                plan.Excluded.Add(new ExcludedItem(p.Item, p.Info, ActionPolicyApplier.DropReason(profile.Thresholds.Policy), false));
                continue;
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
