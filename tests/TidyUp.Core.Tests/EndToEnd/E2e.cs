using TidyUp.Core.Execution;
using TidyUp.Core.Lists;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Execution;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Solving;
using TidyUp.Core.Planning;
using TidyUp.Core.Rules;

namespace TidyUp.Core.Tests.EndToEnd;

/// <summary>The steps a player goes through, as one-liners: plan, tick, clean, come back later; preview, organize.</summary>
internal static class E2e
{
    public static readonly RunIdentity Who = new(0xC0FFEE, "Test Char");

    public const ulong RetA = 0xA;
    public const ulong RetB = 0xB;
    public static readonly StorageId Bags = new(ContainerKind.Inventory);
    public static readonly StorageId Armoury = new(ContainerKind.Armoury);
    public static readonly StorageId Saddle = new(ContainerKind.Saddlebag);
    public static readonly StorageId RetainerA = new(ContainerKind.Retainer, RetA);
    public static readonly StorageId RetainerB = new(ContainerKind.Retainer, RetB);
    public static readonly IReadOnlyDictionary<ulong, string> RetainerNames = new Dictionary<ulong, string> { [RetA] = "Alpha", [RetB] = "Bravo" };

    // Items the shared fixtures lack: the organizer's starter layout needs materia, crystals and housing.
    public const uint Materia = 100;
    public const uint Crystal = 101;
    public const uint Table = 102;
    public const uint UltimateToken = 103;
    private static readonly Dictionary<uint, ItemInfo> Extra = new()
    {
        [Materia] = ItemInfo.Test(Materia, "Savage Aim Materia XII", vendor: 0, marketable: true, category: "Materia", stack: 999),
        [Crystal] = ItemInfo.Test(Crystal, "Fire Crystal", vendor: 0, marketable: true, category: "Crystal", stack: 9999),
        [Table] = ItemInfo.Test(Table, "Oak Table", vendor: 100, marketable: true, category: "Furnishing", stack: 1),
        [UltimateToken] = ItemInfo.Test(UltimateToken, "Ultimate Token", vendor: 0, marketable: false, untradable: true, category: "Miscellany", stack: 1),
        [FakeWorld.MateriaItemId] = ItemInfo.Test(FakeWorld.MateriaItemId, "Loose Materia", vendor: 0, marketable: true, category: "Materia", stack: 999),
    };

    public static ItemInfo? Lookup(uint id) => Extra.GetValueOrDefault(id) ?? TestData.Lookup(id);

    // ---------------------------------------------------------------- slots

    public static SlotRef Bag(int slot, uint page = 0) => new(ContainerKind.Inventory, page, slot);
    public static SlotRef Arm(int slot, uint page = GameContainerIds.ArmoryBody) => new(ContainerKind.Armoury, page, slot);
    public static SlotRef Saddlebag(int slot, uint page = GameContainerIds.SaddleBag1) => new(ContainerKind.Saddlebag, page, slot);
    public static SlotRef Ret(ulong retainer, int slot, uint page = GameContainerIds.RetainerPage1) => new(ContainerKind.Retainer, page, slot, retainer);
    public static SlotRef Dresser(int index) => SlotRef.Dresser(index);

    // ---------------------------------------------------------------- cleaner

    public static ItemContext Context(IEnumerable<uint>? gearset = null, IReadOnlyDictionary<uint, MarketPrice>? market = null, IReadOnlyDictionary<uint, bool>? registered = null, IEnumerable<uint>? protectedIds = null)
    {
        var ctx = TestData.Context(gearsetItems: gearset, market: market, registered: registered);
        return protectedIds is null ? ctx : new ItemContext
        {
            CharacterId = ctx.CharacterId, CharacterName = ctx.CharacterName, GearsetItemIds = ctx.GearsetItemIds, PlateItemIds = ctx.PlateItemIds, PlatesLoaded = ctx.PlatesLoaded,
            JobLevels = ctx.JobLevels, ClassJobCategoryJobs = ctx.ClassJobCategoryJobs, MaxGearsetItemLevel = ctx.MaxGearsetItemLevel, RecipesUsing = ctx.RecipesUsing,
            SeasonalItemIds = ctx.SeasonalItemIds, RetiredCurrencyGearIds = ctx.RetiredCurrencyGearIds, MarketPrices = ctx.MarketPrices, Registered = ctx.Registered,
            ProtectedItemIds = new HashSet<uint>(protectedIds),
        };
    }

    public static Profile Profile(PresetName preset = PresetName.Vendor, int largeStackGuard = 200)
    {
        var p = TestData.MakeProfile(preset);
        p.LargeStackGuard = largeStackGuard;
        return p;
    }

    /// <summary>What the review window would show for this world right now.</summary>
    public static RunPlan Plan(FakeWorld world, Profile? profile = null, ItemContext? ctx = null, ItemList? neverTouch = null, ItemList? alwaysClean = null, IReadOnlySet<string>? skips = null)
    {
        var items = world.Items.Where(i => Lookup(i.ItemId) is not null).ToList();
        return new RunPlanner().Build(items, new PlannerInputs
        {
            Context = ctx ?? Context(),
            Profile = profile ?? Profile(),
            InfoLookup = Lookup,
            ProtectList = neverTouch ?? new ItemList(),
            AlwaysDiscardList = alwaysClean ?? new ItemList(),
            SessionSkips = skips ?? new HashSet<string>(),
            IsAvailable = world.IsContainerAvailable,
            RetainerNames = RetainerNames,
            IncludeUnproposed = true,
        });
    }

    public static PlanRow Row(RunPlan plan, uint itemId) => plan.AllRows.Single(r => r.Item.ItemId == itemId);
    public static PlanRow Row(RunPlan plan, SlotRef slot) => plan.AllRows.Single(r => r.Item.Slot == slot);

    /// <summary>Pressing Clean: everything ticked, as the window would send it.</summary>
    public static List<QueuedAction> Queue(RunPlan plan, Func<PlanRow, bool>? filter = null) =>
        plan.AllRows.Where(r => r.Checked && r.IsExecutable && (filter is null || filter(r))).Select(QueuedAction.FromRow).ToList();

    public static Task<RunReport> Clean(FakeWorld world, IReadOnlyList<QueuedAction> queue, MemoryRunLog? log = null, ExecutionOptions? options = null, CancellationToken ct = default) =>
        new ExecutionEngine(world, log ?? new MemoryRunLog(), new NoDelay(), options).ExecuteAsync(queue, Who, ct);

    /// <summary>What the coordinator keeps for later: parked rows plus follow-ups of items brought home.</summary>
    public static List<QueuedAction> Leftover(RunReport report) => report.Pending.Concat(report.Moved).ToList();

    // ---------------------------------------------------------------- organizer

    public static OrganizerRule Rule(string name, OrganizerPredicate when, Destination then, int keepInBags = 0) =>
        new() { Name = name, When = when, Then = then, KeepInBags = keepInBags };

    public static OrganizerPlan Layout(params OrganizerRule[] rules)
    {
        var plan = new OrganizerPlan { Name = "scenario", BagStagingReserve = 10 };
        plan.Rules.AddRange(rules);
        return plan;
    }

    /// <summary>The organizer preview: rules → desired state → capacity → moves.</summary>
    public static SolveResult Preview(FakeWorld world, OrganizerPlan layout, ItemContext? ctx = null, Func<uint, bool, bool>? neverTouch = null, params ulong[] knownRetainers)
    {
        var retainers = knownRetainers.Length == 0 ? new[] { RetA, RetB } : knownRetainers;
        var items = world.Items.Where(i => Lookup(i.ItemId) is not null).ToList();
        var desired = DesiredStateBuilder.Build(items, layout, ctx ?? Context(), Lookup, neverTouch ?? ((_, _) => false), retainers);
        var storages = new[] { Bags, Armoury, Saddle }.Concat(retainers.Select(r => new StorageId(ContainerKind.Retainer, r)));
        var spaces = CapacityModel.Build(items, storages, Lookup, world.LiveSizes());
        return MoveSolver.Solve(desired, spaces, layout);
    }

    public static Task<MoveRunReport> Organize(FakeWorld world, IReadOnlyList<MoveOp> moves, MemoryMoveLog? log = null, CancellationToken ct = default) =>
        new MoveExecutor(world, log ?? new MemoryMoveLog(), new NoDelay(), relays: world.Relays).ExecuteAsync(moves, Who, ct);

    /// <summary>
    /// Plays the hands-free organizer: keeps opening whichever storage the remaining moves need and running
    /// them until nothing is left or nothing changes. Returns how many rounds it took.
    /// </summary>
    public static async Task<int> OrganizeEverywhere(FakeWorld world, IReadOnlyList<MoveOp> moves, MemoryMoveLog? log = null, int maxRounds = 12)
    {
        var remaining = moves.ToList();
        var rounds = 0;
        while (remaining.Count > 0 && rounds < maxRounds)
        {
            rounds++;
            var need = remaining.Select(m => m.RequiresOpen).FirstOrDefault(s => s is not null);
            world.LeaveBell();
            world.SaddlebagOpen = false;
            if (need is { } s)
            {
                if (s.Kind == ContainerKind.Saddlebag) world.SaddlebagOpen = true;
                else if (s.Kind == ContainerKind.Retainer) world.OpenRetainer(s.OwnerId);
            }
            var report = await Organize(world, remaining, log);
            if (report.Pending.Count == remaining.Count && report.Done == 0) break;
            remaining = report.Pending.ToList();
        }
        return rounds;
    }
}
