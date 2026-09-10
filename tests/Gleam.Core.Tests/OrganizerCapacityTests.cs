using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

public class OrganizerCapacityTests
{
    private static readonly StorageId Bags = new(ContainerKind.Inventory);
    private static readonly StorageId Saddle = new(ContainerKind.Saddlebag);

    [Fact]
    public void Defaults_size_bags_saddlebag_and_retainers_like_the_game()
    {
        var spaces = CapacityModel.Build([], [Bags, Saddle, new StorageId(ContainerKind.Retainer, 0xBEEF), new StorageId(ContainerKind.Armoury)], Lookup);
        Assert.Equal(140, spaces[Bags].Size);
        Assert.Equal(70, spaces[Saddle].Size);
        Assert.Equal(175, spaces[new StorageId(ContainerKind.Retainer, 0xBEEF)].Size);
        Assert.Equal(35 * 10 + 50, spaces[new StorageId(ContainerKind.Armoury)].Size);
        Assert.All(spaces.Values, s => Assert.False(s.SizesAreLive));
    }

    [Fact]
    public void Live_sizes_win_and_add_premium_saddlebag_pages()
    {
        var live = new Dictionary<(StorageId, uint), int>
        {
            [(Saddle, GameContainerIds.SaddleBag1)] = 35,
            [(Saddle, GameContainerIds.SaddleBag2)] = 35,
            [(Saddle, GameContainerIds.PremiumSaddleBag1)] = 35,
            [(Saddle, GameContainerIds.PremiumSaddleBag2)] = 35,
        };
        var spaces = CapacityModel.Build([], [Saddle], Lookup, live);
        Assert.Equal(140, spaces[Saddle].Size);
        Assert.True(spaces[Saddle].SizesAreLive);
    }

    [Fact]
    public void Occupied_slots_and_stack_headroom_are_counted()
    {
        // item 15: stackable widget, stack 99. Two partial stacks of 60 and 50 have 39 and 49 room; a move
        // merges into one stack only, so the headroom that matters is the roomiest single stack.
        var items = new[] { ScannedItem.Simple(Inv(0), 15, 60), ScannedItem.Simple(Inv(5), 15, 50), ScannedItem.Simple(Inv(7), 4, 1) };
        var bags = CapacityModel.Build(items, [Bags], Lookup)[Bags];

        Assert.Equal(3, bags.Used);
        Assert.Equal(137, bags.Free);
        Assert.Equal(49, bags.Headroom(15, false));
        Assert.Equal(0, bags.Headroom(15, true));
        Assert.Equal(0, bags.SlotsNeeded(15, false, 49, 99));    // fits the roomier stack
        Assert.Equal(1, bags.SlotsNeeded(15, false, 60, 99));    // 88 in total, but no single stack takes 60
    }

    [Fact]
    public void Incoming_stacks_merge_into_headroom_before_costing_slots()
    {
        var items = new[] { ScannedItem.Simple(Inv(0), 15, 60) }; // headroom 39
        var bags = CapacityModel.Build(items, [Bags], Lookup)[Bags];

        Assert.Equal(0, bags.SlotsNeeded(15, false, 30, 99));    // fits entirely in the partial stack
        Assert.Equal(1, bags.SlotsNeeded(15, false, 40, 99));    // too big for it: lands in a slot of its own
        Assert.Equal(1, bags.SlotsNeeded(4, false, 1, 1));       // gear never merges

        var used = bags.Accept(15, false, 40, 99);
        Assert.Equal(1, used);
        Assert.Equal(2, bags.Used);
        // The 40 sit in a new stack with 59 room; the old stack still has 39. Roomiest single stack: 59.
        Assert.Equal(59, bags.Headroom(15, false));

        Assert.Equal(0, bags.Accept(15, false, 30, 99));          // merges into the 39-room stack, the tighter fit
        Assert.Equal(59, bags.Headroom(15, false));
        Assert.Equal(2, bags.Used);
    }

    [Fact]
    public void Release_frees_the_slot_and_its_headroom()
    {
        var stack = ScannedItem.Simple(Inv(0), 15, 60);
        var bags = CapacityModel.Build([stack], [Bags], Lookup)[Bags];
        bags.Release(stack, Lookup(15));
        Assert.Equal(0, bags.Used);
        Assert.Equal(0, bags.Headroom(15, false));
    }

    [Fact]
    public void A_full_container_refuses_and_fits_reports_false()
    {
        var items = Enumerable.Range(0, 175).Select(i => ScannedItem.Simple(Ret(i % 25, 0xBEEF).With(page: (uint)(GameContainerIds.RetainerPage1 + i / 25)), 4, 1)).ToList();
        var id = new StorageId(ContainerKind.Retainer, 0xBEEF);
        var ret = CapacityModel.Build(items, [id], Lookup)[id];
        Assert.Equal(0, ret.Free);
        Assert.False(ret.Fits(4, false, 1, 1));
        Assert.Throws<InvalidOperationException>(() => ret.Accept(4, false, 1, 1));
    }
}

internal static class SlotRefTestExtensions
{
    public static SlotRef With(this SlotRef s, uint page) => new(s.Kind, page, s.Slot, s.OwnerId);
}
