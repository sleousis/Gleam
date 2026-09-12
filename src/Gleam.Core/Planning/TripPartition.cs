using Gleam.Core.Execution;
using Gleam.Core.Model;
using Gleam.Core.Settings;

namespace Gleam.Core.Planning;

/// <summary>
/// How a hands-free clean splits the ticked queue into the stops of one trip. Bag and armoury rows that need no
/// NPC are done where the player stands; sells, seals and market listings wait for the merchant, the Grand Company
/// or a retainer; every other container's rows go to that container's own stop. The lists are the run's working
/// lists: later stops add to and take from them.
/// </summary>
public sealed class TripPartition
{
    /// <summary>Bag and armoury rows done where the player stands: discards, desynthesis.</summary>
    public List<QueuedAction> Here { get; private init; } = new();

    /// <summary>Bag and armoury rows for the market board, listed through whichever retainer has room.</summary>
    public List<QueuedAction> Listings { get; private init; } = new();

    /// <summary>Bag and armoury rows sold through a retainer or, failing that, to a merchant.</summary>
    public List<QueuedAction> Sells { get; private init; } = new();

    /// <summary>Bag and armoury rows turned in to the Grand Company for seals.</summary>
    public List<QueuedAction> Seals { get; private init; } = new();

    public List<QueuedAction> Saddlebag { get; private init; } = new();

    /// <summary>Each retainer's own rows, by retainer id.</summary>
    public Dictionary<ulong, List<QueuedAction>> Retainers { get; private init; } = new();

    public List<QueuedAction> Dresser { get; private init; } = new();

    /// <summary>Actions that need an NPC's window rather than the item menu alone.</summary>
    public static bool NeedsNpc(ActionKind action) => action is ActionKind.VendorSell or ActionKind.ExpertDelivery or ActionKind.MarketList;

    public static TripPartition Of(IEnumerable<QueuedAction> queue)
    {
        var all = queue.ToList();
        return new TripPartition
        {
            Here = all.Where(q => q.Kind.IsAlwaysLoaded() && !NeedsNpc(q.Action)).ToList(),
            Listings = all.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.MarketList).ToList(),
            Sells = all.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.VendorSell).ToList(),
            Seals = all.Where(q => q.Kind.IsAlwaysLoaded() && q.Action == ActionKind.ExpertDelivery).ToList(),
            Saddlebag = all.Where(q => q.Kind == ContainerKind.Saddlebag).ToList(),
            Retainers = all.Where(q => q.Kind == ContainerKind.Retainer).GroupBy(q => q.Slot.OwnerId).ToDictionary(g => g.Key, g => g.ToList()),
            Dresser = all.Where(q => q.Kind == ContainerKind.GlamourDresser).ToList(),
        };
    }

    /// <summary>
    /// Whether a run also travels to containers with nothing ticked, to scan and clean them. Off: a run only goes
    /// where the ticked rows are, so ticking one bag item never triggers a trip to the inn.
    /// </summary>
    public static bool Sweeps(bool visitContainersWithoutRows, UnseenRowsMode mode) => visitContainersWithoutRows && mode != UnseenRowsMode.Skip;

    public bool GoesToSaddlebag(bool openSaddlebag, bool sweep) => openSaddlebag && (Saddlebag.Count > 0 || sweep);

    /// <summary>Retainers, market listings and the dresser are all reached from an inn room.</summary>
    public bool NeedsInn(bool visitRetainers, bool visitDresser, bool sweep) =>
        (visitRetainers && (Retainers.Count > 0 || Listings.Count > 0 || sweep)) || (visitDresser && (Dresser.Count > 0 || sweep));

    /// <summary>Items a retainer handed back (for materia, a vendor, or seals): they finish from the bags once the retainer is closed.</summary>
    public static List<QueuedAction> BroughtBack(IEnumerable<QueuedAction> pending) =>
        // A listing needs a retainer's sell list, and the trip has left the bell by now, so it keeps waiting for the
        // next run. It used to be taken off the waiting list here and routed nowhere, and silently disappeared.
        pending.Where(p => p.Kind.IsAlwaysLoaded() && p.BroughtHome && p.Action != ActionKind.MarketList).ToList();

    /// <summary>Where the items brought back go: the merchant, the Grand Company, or straight from the bags.</summary>
    public static (List<QueuedAction> Sells, List<QueuedAction> Seals, List<QueuedAction> Here) SplitBroughtBack(IReadOnlyList<QueuedAction> broughtBack) => (
        broughtBack.Where(b => b.Action == ActionKind.VendorSell).ToList(),
        broughtBack.Where(b => b.Action == ActionKind.ExpertDelivery).ToList(),
        broughtBack.Where(b => !NeedsNpc(b.Action)).ToList());

    /// <summary>A retainer's own rows: cleaned while its inventory is open, then its own listings from its sell list.</summary>
    public static (List<QueuedAction> Rows, List<QueuedAction> OwnListings) SplitRetainer(IReadOnlyList<QueuedAction> allRows) => (
        allRows.Where(r => r.Action != ActionKind.MarketList).ToList(),
        allRows.Where(r => r.Action == ActionKind.MarketList).ToList());
}
