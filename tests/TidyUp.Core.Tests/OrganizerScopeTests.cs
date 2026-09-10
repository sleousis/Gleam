using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Core.Organizer.Capacity;
using TidyUp.Core.Organizer.Execution;
using TidyUp.Core.Organizer.Model;
using TidyUp.Core.Organizer.Solving;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

/// <summary>What the organizer may reach: the retainers and places the player allows, and nothing else.</summary>
public class OrganizerScopeTests
{
    private static bool NoNeverTouch(uint id, bool hq) => false;

    private static OrganizerPlan Plan(OrganizerPredicate when, Destination then)
    {
        var plan = new OrganizerPlan { Name = "test" };
        plan.Rules.Add(new OrganizerRule { Name = "rule", When = when, Then = then });
        return plan;
    }

    [Fact]
    public void A_retainer_the_player_left_alone_receives_nothing()
    {
        var plan = Plan(new OrganizerPredicate { ItemIds = [6] }, Destination.RetainerNamed(0xA));

        var desired = DesiredStateBuilder.Build([ScannedItem.Simple(Inv(0), 6, 1)], plan, Context(), Lookup, NoNeverTouch, [0xAul],
            excludedRetainers: new HashSet<ulong> { 0xA });

        var pinned = Assert.Single(desired.Pinned);
        Assert.Contains("leave that retainer alone", pinned.Reason);
    }

    [Fact]
    public void Nothing_is_taken_out_of_a_retainer_the_player_left_alone()
    {
        var plan = Plan(new OrganizerPredicate { ItemIds = [6] }, Destination.Bags);

        var desired = DesiredStateBuilder.Build([ScannedItem.Simple(Ret(0, 0xA), 6, 1)], plan, Context(), Lookup, NoNeverTouch, [0xAul],
            excludedRetainers: new HashSet<ulong> { 0xA });

        Assert.DoesNotContain(desired.Placements, p => p.WantsMove);
    }

    [Fact]
    public void Crystals_stay_in_their_pouch()
    {
        var plan = Plan(new OrganizerPredicate { Tags = [ItemTag.Crystals] }, Destination.Bags);

        var desired = DesiredStateBuilder.Build([ScannedItem.Simple(Saddle(0), 23, 500)], plan, Context(), Lookup, NoNeverTouch, []);

        Assert.Contains(desired.Pinned, p => p.Reason.Contains("pouch"));
        Assert.DoesNotContain(desired.Placements, p => p.WantsMove);
    }

    [Fact]
    public void A_place_the_player_said_not_to_open_is_left_out()
    {
        var plan = Plan(new OrganizerPredicate { ItemIds = [6] }, Destination.Saddlebag);

        var desired = DesiredStateBuilder.Build([ScannedItem.Simple(Inv(0), 6, 1), ScannedItem.Simple(Saddle(1), 6, 1)], plan, Context(), Lookup, NoNeverTouch, [],
            mayOpen: k => k != ContainerKind.Saddlebag);

        Assert.Contains(desired.Pinned, p => p.Reason.Contains("may not open"));
        Assert.DoesNotContain(desired.Placements, p => p.Item.Slot.Kind == ContainerKind.Saddlebag);
    }

    [Fact]
    public async Task A_manual_run_moves_on_to_the_next_round_once_the_first_is_done()
    {
        var game = new FakeMoveActions();
        var saddle = new StorageId(ContainerKind.Saddlebag);
        var bags = new StorageId(ContainerKind.Inventory);
        game.Open.Add(saddle);
        var a = ScannedItem.Simple(Inv(0), 6, 1);
        var b = ScannedItem.Simple(Inv(1), 6, 1);
        game.Slots[a.Slot] = a;
        game.Slots[b.Slot] = b;
        var first = new MoveOp(Guid.NewGuid(), a, Lookup(6)!, bags, saddle, MoveLeg.Direct, 0, 1);
        var second = new MoveOp(Guid.NewGuid(), b, Lookup(6)!, bags, saddle, MoveLeg.Direct, 0, 2);

        var report = await new MoveExecutor(game, new MemoryMoveLog(), new NoDelay()).ExecuteAsync([first, second], new RunIdentity(1, "Someone"), CancellationToken.None);

        Assert.Equal(2, report.Done);
    }
}
