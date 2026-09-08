using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Model;

namespace TidyUp.Core.Organizer.Solving;

/// <summary>Where one stack should end up, and which rule said so (null = the plan's fallback).</summary>
public sealed record Placement(ScannedItem Item, ItemInfo Info, Destination Destination, OrganizerRule? Rule)
{
    public StorageId Current => StorageId.Of(Item.Slot);

    /// <summary>True when the destination is somewhere else than the item already is (or is still to be chosen).</summary>
    public bool WantsMove => Destination.Kind != DestinationKind.Stay && (Destination.Storage is null || Destination.Storage != Current);
}

/// <summary>A stack the organizer will not move, and why.</summary>
public sealed record PinnedItem(ScannedItem Item, ItemInfo Info, string Reason);

public sealed class DesiredState
{
    public List<Placement> Placements { get; } = new();
    public List<PinnedItem> Pinned { get; } = new();
}

/// <summary>
/// Applies a plan's rules to a snapshot: first matching rule wins, unmatched stacks follow the fallback.
/// Things that must not move are pinned with a reason instead of placed.
/// </summary>
public static class DesiredStateBuilder
{
    public static DesiredState Build(
        IEnumerable<ScannedItem> items,
        OrganizerPlan plan,
        ItemContext ctx,
        Func<uint, ItemInfo?> infoLookup,
        Func<uint, bool, bool> onNeverTouch,
        IReadOnlyCollection<ulong> knownRetainers)
    {
        var state = new DesiredState();
        var retainersInScope = plan.RetainersInScope.Count == 0 ? new HashSet<ulong>(knownRetainers) : plan.RetainersInScope;
        var rules = plan.Rules.Where(r => r.Enabled).ToList();

        foreach (var item in items)
        {
            if (item.Slot.Kind == ContainerKind.GlamourDresser) continue;
            if (item.Slot.Kind == ContainerKind.Retainer && !retainersInScope.Contains(item.Slot.OwnerId)) continue;
            var info = infoLookup(item.ItemId);
            if (info is null) continue;

            var rule = rules.FirstOrDefault(r => r.When.Matches(item, info, ctx, onNeverTouch));
            var destination = rule?.Then ?? plan.Fallback;

            if (destination.Kind == DestinationKind.Retainer && destination.RetainerId != 0 && !retainersInScope.Contains(destination.RetainerId))
            {
                state.Pinned.Add(new PinnedItem(item, info, "Its rule names a retainer that is not in this plan"));
                continue;
            }

            var reason = WhyPinned(item, info, ctx, destination);
            if (reason is not null)
            {
                state.Pinned.Add(new PinnedItem(item, info, reason));
                continue;
            }

            state.Placements.Add(new Placement(item, info, destination, rule));
        }

        return state;
    }

    /// <summary>A reason the stack cannot go to the destination, or null. Staying put is always allowed.</summary>
    private static string? WhyPinned(ScannedItem item, ItemInfo info, ItemContext ctx, Destination to)
    {
        if (to.Kind == DestinationKind.Stay) return null;
        var current = StorageId.Of(item.Slot);
        if (to.Storage is { } target && target == current) return null;

        if (info.IsEquipment && ctx.GearsetItemIds.Contains(info.ItemId) && to.Kind is DestinationKind.Saddlebag or DestinationKind.Retainer)
            return "Part of a gearset; gearsets only see the bags and armoury";
        if (to.Kind == DestinationKind.Armoury && info.ArmouryPage == 0)
            return "Only equipment goes in the armoury chest";
        if (info.UiCategory.Equals("Soul Crystal", StringComparison.OrdinalIgnoreCase) && to.Kind is DestinationKind.Saddlebag or DestinationKind.Retainer)
            return "Soul crystals stay with you";
        return null;
    }
}
