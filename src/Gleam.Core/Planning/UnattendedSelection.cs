using Gleam.Core.Execution;
using Gleam.Core.Model;
using Gleam.Core.Settings;

namespace Gleam.Core.Planning;

/// <summary>
/// Which rows Gleam may act on while nobody is looking at the list: rows a hands-free run only finds once a
/// container opens, and junk that lands in the bags after a retainer's ventures. Both pick their own rows, the
/// ones a rule would tick on its own, and never trust a tick that may be the player's, made for another moment.
/// </summary>
public static class UnattendedSelection
{
    /// <summary>An item as the review showed it: by container and item rather than slot, because sorting and a retainer's real slot numbers move things.</summary>
    public static (ContainerKind Kind, ulong OwnerId, uint ItemId, bool IsHq) Identity(PlanRow r) =>
        (r.Item.Slot.Kind, r.Item.Slot.OwnerId, r.Item.ItemId, r.Item.IsHq);

    /// <summary>Every item the review showed when a run began.</summary>
    public static HashSet<(ContainerKind, ulong, uint, bool)> Reviewed(IEnumerable<PlanRow> rows) => rows.Select(Identity).ToHashSet();

    /// <summary>What a rule would tick on its own, for an item the player has not skipped this session.</summary>
    public static bool RuleWouldTick(PlanRow r, IReadOnlySet<string> sessionSkips) =>
        r.IsSuggested && r.Proposal.DefaultChecked && !sessionSkips.Contains(r.Key);

    /// <summary>Whether the pilot cleans rows it only finds once a container is open. Otherwise they wait for the next review.</summary>
    public static bool CleansUnseen(UnseenRowsMode mode) => mode == UnseenRowsMode.Clean;

    /// <summary>
    /// A row found once a container opened that the pilot may clean. Only items the player never saw: anything
    /// that was in the review was already accepted or declined, and matching it again here by slot re-ticked
    /// items the player had unticked once sorting or a retainer's real slot numbers moved them.
    /// </summary>
    public static bool IsUnseenPick(PlanRow r, IReadOnlySet<string> sessionSkips, IReadOnlySet<(ContainerKind, ulong, uint, bool)> reviewed) =>
        RuleWouldTick(r, sessionSkips) && !reviewed.Contains(Identity(r));

    /// <summary>The queue the pilot cleans in a container that just opened; empty when the mode leaves such rows alone.</summary>
    public static List<QueuedAction> PickUnseen(IEnumerable<PlanRow> rows, UnseenRowsMode mode, IReadOnlySet<string> sessionSkips,
        IReadOnlySet<(ContainerKind, ulong, uint, bool)> reviewed) =>
        CleansUnseen(mode) ? PlanQueue.Build(rows, r => IsUnseenPick(r, sessionSkips, reviewed), requireChecked: false) : new List<QueuedAction>();

    /// <summary>After a retainer's ventures: discards from the bags only, nothing that needs a merchant or a window.</summary>
    public static bool IsVenturePick(PlanRow r, IReadOnlySet<string> sessionSkips) =>
        r.Item.Slot.Kind == ContainerKind.Inventory && r.ChosenAction == ActionKind.Discard && RuleWouldTick(r, sessionSkips);

    public static List<QueuedAction> PickAfterVentures(IEnumerable<PlanRow> rows, IReadOnlySet<string> sessionSkips) =>
        PlanQueue.Build(rows, r => IsVenturePick(r, sessionSkips), requireChecked: false);
}
