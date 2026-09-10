using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Model;

namespace Gleam.Core.Organizer.Solving;

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
        IReadOnlyCollection<ulong> knownRetainers,
        IReadOnlySet<ulong>? excludedRetainers = null,
        Func<ContainerKind, bool>? mayOpen = null)
    {
        var state = new DesiredState();
        var retainersInScope = new HashSet<ulong>(plan.RetainersInScope.Count == 0 ? knownRetainers : plan.RetainersInScope);
        // A retainer the player told Gleam to leave alone is out of every layout, whatever the layout says.
        if (excludedRetainers is not null) retainersInScope.ExceptWith(excludedRetainers);
        var rules = plan.Rules.Where(r => r.Enabled).ToList();

        var matched = new List<Match>();
        foreach (var item in items)
        {
            if (item.Slot.Kind == ContainerKind.GlamourDresser) continue;
            // Places the player said Gleam may not open are left exactly as they are.
            if (mayOpen is not null && !item.Slot.Kind.IsAlwaysLoaded() && !mayOpen(item.Slot.Kind)) continue;
            if (item.Slot.Kind == ContainerKind.Retainer && !retainersInScope.Contains(item.Slot.OwnerId)) continue;
            var info = infoLookup(item.ItemId);
            if (info is null) continue;

            var rule = rules.FirstOrDefault(r => r.When.Matches(item, info, ctx, onNeverTouch));
            matched.Add(new Match(item, info, rule, rule?.Then ?? plan.Fallback));
        }

        var kept = StacksKeptInBags(matched);
        foreach (var (item, info, rule, wanted) in matched)
        {
            var destination = kept.Contains(item.Slot) ? Destination.Bags : wanted;
            var destKind = destination.IsAnyRetainer ? ContainerKind.Retainer : destination.Storage?.Kind;
            var alreadyThere = destination.IsAnyRetainer
                ? item.Slot.Kind == ContainerKind.Retainer
                : destination.Storage is { } ds && ds.Kind == item.Slot.Kind && (ds.Kind != ContainerKind.Retainer || ds.OwnerId == item.Slot.OwnerId);

            // Crystals keep to a pouch of their own. Moving one into the bags, or through them on the way to a
            // retainer, asks the game for a bag slot a crystal can never take.
            if (!alreadyThere && destination.Kind != DestinationKind.Stay && ItemTags.Of(info) == ItemTag.Crystals)
            {
                state.Pinned.Add(new PinnedItem(item, info, "Crystals keep to a pouch of their own, so Gleam leaves them where they are"));
                continue;
            }
            if (!alreadyThere && mayOpen is not null && destKind is { } dk && !dk.IsAlwaysLoaded() && !mayOpen(dk))
            {
                state.Pinned.Add(new PinnedItem(item, info, $"Settings say Gleam may not open the {dk.DisplayName().ToLowerInvariant()}"));
                continue;
            }
            if (!alreadyThere && destination.Kind == DestinationKind.Retainer && destination.RetainerId != 0 && excludedRetainers?.Contains(destination.RetainerId) == true)
            {
                state.Pinned.Add(new PinnedItem(item, info, "You told Gleam to leave that retainer alone"));
                continue;
            }

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

    private readonly record struct Match(ScannedItem Item, ItemInfo Info, OrganizerRule? Rule, Destination Destination);

    /// <summary>
    /// For rules with a "keep N in the bags" number: the bag stacks of each matched item that stay behind.
    /// Smallest stacks first so the kept amount lands as close to N as whole stacks allow; when even the
    /// smallest stack is over N, that one stays, because stacks are never split.
    /// </summary>
    private static HashSet<SlotRef> StacksKeptInBags(List<Match> matched)
    {
        var keep = new HashSet<SlotRef>();
        var eligible = matched.Where(m =>
            m.Rule is { KeepInBags: > 0 } && m.Item.Slot.Kind == ContainerKind.Inventory
            && m.Destination.Kind is not (DestinationKind.Bags or DestinationKind.Stay));
        foreach (var group in eligible.GroupBy(m => (m.Rule!.Id, m.Item.ItemId, m.Item.IsHq)))
        {
            var limit = group.First().Rule!.KeepInBags;
            var kept = 0;
            foreach (var m in group.OrderBy(m => m.Item.Quantity))
            {
                if (kept > 0 && kept + m.Item.Quantity > limit) break;
                keep.Add(m.Item.Slot);
                kept += m.Item.Quantity;
            }
        }
        return keep;
    }

    /// <summary>A reason the stack cannot go to the destination, or null. Staying put is always allowed.</summary>
    private static string? WhyPinned(ScannedItem item, ItemInfo info, ItemContext ctx, Destination to)
    {
        if (to.Kind == DestinationKind.Stay) return null;
        var current = StorageId.Of(item.Slot);
        if (to.Storage is { } target && target == current) return null;

        if (info.IsEquipment && ctx.GearsetItemIds.Contains(info.ItemId) && to.Kind is DestinationKind.Saddlebag or DestinationKind.Retainer)
            return "Part of a gear set. Gear sets only see the bags and armoury";
        if (to.Kind == DestinationKind.Armoury && info.ArmouryPage == 0)
            return "Only equipment goes in the armoury chest";
        if (info.UiCategory.Equals("Soul Crystal", StringComparison.OrdinalIgnoreCase) && to.Kind is DestinationKind.Saddlebag or DestinationKind.Retainer)
            return "Soul crystals stay with you";
        return null;
    }
}
