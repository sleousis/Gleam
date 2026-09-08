using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Model;

namespace TidyUp.Core.Organizer.Solving;

public enum MoveLeg
{
    /// <summary>Bags or armoury on one side, so the game can do it in one step.</summary>
    Direct,
    /// <summary>First half of a relay: out of a storage into the bags.</summary>
    RelayOut,
    /// <summary>Second half of a relay: out of the bags into the destination.</summary>
    RelayIn,
}

/// <summary>One physical move the executor will perform. Relayed moves share a <see cref="MoveId"/> across their two legs.</summary>
public sealed record MoveOp(Guid MoveId, ScannedItem Item, ItemInfo Info, StorageId From, StorageId To, MoveLeg Leg, uint PreferredPage, int Pass)
{
    /// <summary>The storage that must be open for this leg (saddlebag or a retainer), or null when bags and armoury suffice.</summary>
    public StorageId? RequiresOpen =>
        !From.Kind.IsAlwaysLoaded() ? From : !To.Kind.IsAlwaysLoaded() ? To : null;
}

public sealed record Shortfall(StorageId Storage, int Needed, int Free, string TopRule)
{
    public int Short => Needed - Free;
}

public sealed class FeasibilityReport
{
    public List<Shortfall> Shortfalls { get; } = new();
    public bool Feasible => Shortfalls.Count == 0;
}

public sealed record StorageEndState(StorageId Storage, int Size, int UsedBefore, int UsedAfter, bool SizesAreLive);

public sealed class SolveResult
{
    public List<MoveOp> Moves { get; } = new();
    public FeasibilityReport Report { get; } = new();
    public List<StorageEndState> EndState { get; } = new();
    public List<PinnedItem> Pinned { get; } = new();
    /// <summary>Stacks that had a destination but no room there; they stay where they are.</summary>
    public List<PinnedItem> NoRoom { get; } = new();
    public int Passes { get; set; } = 1;
    public int RelayedMoves => Moves.Count(m => m.Leg == MoveLeg.RelayOut);
    public IEnumerable<StorageId> StoragesToOpen => Moves.Select(m => m.RequiresOpen).OfType<StorageId>().Distinct();
}

/// <summary>
/// Turns a desired state into an ordered move list against a capacity model. Pure and deterministic.
/// Bag-outbound moves first (they free the staging area), then relays in waves sized to the bag reserve,
/// then moves into the bags and armoury. Every step is simulated so the bags never overflow on paper.
/// </summary>
public static class MoveSolver
{
    private static readonly StorageId BagsId = new(ContainerKind.Inventory);
    private static readonly StorageId ArmouryId = new(ContainerKind.Armoury);

    public static SolveResult Solve(DesiredState desired, IReadOnlyDictionary<StorageId, ContainerSpace> spaces, OrganizerPlan plan)
    {
        var result = new SolveResult();
        result.Pinned.AddRange(desired.Pinned);
        var sim = spaces.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        var before = spaces.ToDictionary(kv => kv.Key, kv => kv.Value.Used);
        if (!sim.ContainsKey(BagsId)) sim[BagsId] = new ContainerSpace(BagsId);

        // 1. Which stacks move, and where exactly. "Any retainer" is resolved here against the simulated space.
        var pending = new List<(Placement P, StorageId To, uint Page)>();
        var retainers = sim.Keys.Where(k => k.Kind == ContainerKind.Retainer).OrderBy(k => k.OwnerId).ToList();
        foreach (var p in desired.Placements.Where(p => p.WantsMove))
        {
            StorageId? to = p.Destination.Storage;
            if (p.Destination.IsAnyRetainer)
            {
                var inScope = plan.RetainersInScope.Count == 0 ? retainers : retainers.Where(r => plan.RetainersInScope.Contains(r.OwnerId)).ToList();
                if (inScope.Contains(p.Current)) continue; // already with a retainer: that counts as placed
                to = inScope
                    .Where(r => sim[r].Fits(p.Item.ItemId, p.Item.IsHq, p.Item.Quantity, p.Info.StackSize, plan.MergeStacksAtDestination))
                    .OrderByDescending(r => sim[r].Headroom(p.Item.ItemId, p.Item.IsHq) > 0)
                    .ThenByDescending(r => sim[r].Free)
                    .Select(r => (StorageId?)r)
                    .FirstOrDefault();
                if (to is null)
                {
                    result.NoRoom.Add(new PinnedItem(p.Item, p.Info, "No retainer has room for it"));
                    continue;
                }
            }
            if (to is null) continue;
            var page = to.Value.Kind == ContainerKind.Armoury ? p.Info.ArmouryPage : 0u;
            if (!sim.ContainsKey(to.Value)) sim[to.Value] = NewSpace(to.Value);
            // Reserve the space now so later "any retainer" choices see it taken.
            if (p.Destination.IsAnyRetainer) sim[to.Value].Accept(p.Item.ItemId, p.Item.IsHq, p.Item.Quantity, p.Info.StackSize, plan.MergeStacksAtDestination);
            pending.Add((p, to.Value, page));
        }

        // 2. Net feasibility per storage: outgoing space is credited before incoming is charged.
        var check = spaces.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());
        foreach (var (p, to, _) in pending)
            if (check.TryGetValue(p.Current, out var src)) src.Release(p.Item, p.Info);
        var needed = new Dictionary<StorageId, int>();
        var topRule = new Dictionary<StorageId, Dictionary<string, int>>();
        foreach (var (p, to, _) in pending.OrderBy(x => x.To.ToString()))
        {
            if (!check.ContainsKey(to)) check[to] = NewSpace(to);
            var space = check[to];
            var slots = space.SlotsNeeded(p.Item.ItemId, p.Item.IsHq, p.Item.Quantity, p.Info.StackSize, plan.MergeStacksAtDestination);
            needed[to] = needed.GetValueOrDefault(to) + slots;
            var ruleName = p.Rule?.Name ?? "fallback";
            topRule.TryAdd(to, new Dictionary<string, int>());
            topRule[to][ruleName] = topRule[to].GetValueOrDefault(ruleName) + slots;
            if (space.Fits(p.Item.ItemId, p.Item.IsHq, p.Item.Quantity, p.Info.StackSize, plan.MergeStacksAtDestination))
                space.Accept(p.Item.ItemId, p.Item.IsHq, p.Item.Quantity, p.Info.StackSize, plan.MergeStacksAtDestination);
        }
        foreach (var (to, n) in needed)
        {
            var free = spaces.TryGetValue(to, out var s0) ? s0.Free : NewSpace(to).Free;
            free += pending.Where(x => x.P.Current == to).Count(); // slots the storage itself gives up
            if (n > free)
                result.Report.Shortfalls.Add(new Shortfall(to, n, free, topRule[to].OrderByDescending(kv => kv.Value).First().Key));
        }

        // 3. Legs. Anything touching the bags or armoury is direct; everything else relays through the bags.
        var ops = new List<MoveOp>();
        foreach (var (p, to, page) in pending)
        {
            var id = Guid.NewGuid();
            if (p.Current.Kind.IsAlwaysLoaded() || to.Kind.IsAlwaysLoaded())
                ops.Add(new MoveOp(id, p.Item, p.Info, p.Current, to, MoveLeg.Direct, page, 1));
            else
            {
                ops.Add(new MoveOp(id, p.Item, p.Info, p.Current, BagsId, MoveLeg.RelayOut, 0, 1));
                ops.Add(new MoveOp(id, p.Item, p.Info, BagsId, to, MoveLeg.RelayIn, page, 1));
            }
        }

        // 4. Order: out of bags/armoury first (grouped by destination), then relays in waves, then into bags/armoury.
        var bags = sim[BagsId];
        var ordered = new List<MoveOp>();
        var outbound = ops.Where(o => o.Leg == MoveLeg.Direct && o.From.Kind.IsAlwaysLoaded() && !o.To.Kind.IsAlwaysLoaded()).OrderBy(o => o.To.ToString()).ToList();
        var between = ops.Where(o => o.Leg == MoveLeg.Direct && o.From.Kind.IsAlwaysLoaded() && o.To.Kind.IsAlwaysLoaded()).ToList();
        var inbound = ops.Where(o => o.Leg == MoveLeg.Direct && !o.From.Kind.IsAlwaysLoaded()).OrderBy(o => o.From.ToString()).ToList();
        var relays = ops.Where(o => o.Leg == MoveLeg.RelayOut).OrderBy(o => o.From.ToString()).ThenBy(o => ops.First(x => x.MoveId == o.MoveId && x.Leg == MoveLeg.RelayIn).To.ToString()).ToList();
        var relayIn = ops.Where(o => o.Leg == MoveLeg.RelayIn).ToDictionary(o => o.MoveId);

        foreach (var o in outbound) { if (o.From == BagsId) bags.Release(o.Item, o.Info); ordered.Add(o); }
        foreach (var o in between) { if (o.From == BagsId) bags.Release(o.Item, o.Info); else if (o.To == BagsId) TryAccept(bags, o, plan); ordered.Add(o); }

        var pass = 1;
        var remaining = new Queue<MoveOp>(relays);
        while (remaining.Count > 0)
        {
            var wave = new List<MoveOp>();
            var waveBags = bags.Clone();
            var skipped = new Queue<MoveOp>();
            while (remaining.Count > 0)
            {
                var o = remaining.Dequeue();
                var slots = waveBags.SlotsNeeded(o.Item.ItemId, o.Item.IsHq, o.Item.Quantity, o.Info.StackSize, plan.MergeStacksAtDestination);
                if (slots <= waveBags.Free - plan.BagStagingReserve)
                {
                    waveBags.Accept(o.Item.ItemId, o.Item.IsHq, o.Item.Quantity, o.Info.StackSize, plan.MergeStacksAtDestination);
                    wave.Add(o with { Pass = pass });
                }
                else skipped.Enqueue(o);
            }
            if (wave.Count == 0)
            {
                // Not even one stack fits beside the reserve: a later pass, after the bags are re-scanned.
                pass++;
                if (pass > 3)
                {
                    foreach (var o in skipped) result.NoRoom.Add(new PinnedItem(o.Item, o.Info, "The bags never have room to pass it through"));
                    break;
                }
                // Let the second-leg drains of nothing happen; try again with a clean reserve.
                remaining = skipped;
                continue;
            }
            ordered.AddRange(wave);
            foreach (var o in wave) ordered.Add(relayIn[o.MoveId] with { Pass = pass });
            remaining = skipped;
        }
        result.Passes = Math.Max(1, pass);

        foreach (var o in inbound) { if (o.To == BagsId) TryAccept(bags, o, plan); ordered.Add(o); }
        result.Moves.AddRange(ordered);

        // 5. End state per storage.
        foreach (var (id, space) in check.OrderBy(kv => kv.Key.Kind).ThenBy(kv => kv.Key.OwnerId))
            result.EndState.Add(new StorageEndState(id, space.Size, before.GetValueOrDefault(id), space.Used, space.SizesAreLive));

        return result;
    }

    private static void TryAccept(ContainerSpace space, MoveOp o, OrganizerPlan plan)
    {
        if (space.Fits(o.Item.ItemId, o.Item.IsHq, o.Item.Quantity, o.Info.StackSize, plan.MergeStacksAtDestination))
            space.Accept(o.Item.ItemId, o.Item.IsHq, o.Item.Quantity, o.Info.StackSize, plan.MergeStacksAtDestination);
    }

    private static ContainerSpace NewSpace(StorageId id)
    {
        var s = new ContainerSpace(id);
        foreach (var page in CapacityModel.PagesOf(id.Kind)) s.SetPageSize(page, CapacityModel.DefaultPageSize(page));
        return s;
    }
}
