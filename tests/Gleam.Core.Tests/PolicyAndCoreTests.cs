using Gleam.Core.Execution;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Rules;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

public class ActionPolicyApplierTests
{
    private static Proposal Row(uint itemId, SlotRef slot, ActionKind action = ActionKind.Discard, long marketUnit = 0, Confidence confidence = Confidence.High) => new()
    {
        Item = ScannedItem.Simple(slot, itemId, 3),
        Info = Items[itemId],
        Action = action,
        Alternatives = action == ActionKind.Discard ? [ActionKind.VendorSell] : [ActionKind.Discard],
        Confidence = confidence,
        RuleId = "test",
        Reason = "test",
        MarketUnitPrice = marketUnit,
    };

    [Fact]
    public void Market_board_preset_lists_marketable_sells_tradeable_discards_untradeable()
    {
        var listed = ActionPolicyApplier.Apply(Row(12, Inv(0), marketUnit: 400), ActionPolicy.MarketListTradeable)!;
        Assert.Equal(ActionKind.MarketList, listed.Action);
        Assert.Equal(1200, listed.ValueGil);
        Assert.Contains(ActionKind.Discard, listed.Alternatives);

        var sold = ActionPolicyApplier.Apply(Row(1, Inv(1)), ActionPolicy.MarketListTradeable)!;   // tradeable, not marketable
        Assert.Equal(ActionKind.VendorSell, sold.Action);
        Assert.Equal(28 * 3, sold.ValueGil);

        var discarded = ActionPolicyApplier.Apply(Row(8, Inv(2), ActionKind.VendorSell), ActionPolicy.MarketListTradeable)!; // untradeable
        Assert.Equal(ActionKind.Discard, discarded.Action);
    }

    [Fact]
    public void Market_board_preset_falls_back_to_vendor_when_no_price_is_known()
    {
        var row = ActionPolicyApplier.Apply(Row(12, Inv(0), marketUnit: 0), ActionPolicy.MarketListTradeable)!;
        Assert.Equal(ActionKind.VendorSell, row.Action);
    }

    [Fact]
    public void Vendor_preset_sells_what_it_can_and_discards_the_rest()
    {
        Assert.Equal(ActionKind.VendorSell, ActionPolicyApplier.Apply(Row(1, Inv(0)), ActionPolicy.DiscardUntradeableSellTradeable)!.Action);
        Assert.Equal(ActionKind.Discard, ActionPolicyApplier.Apply(Row(8, Inv(1), ActionKind.VendorSell), ActionPolicy.DiscardUntradeableSellTradeable)!.Action);
    }

    [Fact]
    public void Discard_all_preset_discards_even_sellable_items()
    {
        var row = ActionPolicyApplier.Apply(Row(1, Inv(0), ActionKind.VendorSell), ActionPolicy.DiscardAll)!;
        Assert.Equal(ActionKind.Discard, row.Action);
        Assert.Contains(ActionKind.VendorSell, row.Alternatives);
        Assert.Equal(0, row.ValueGil);
    }

    [Fact]
    public void Policies_leave_keep_rows_and_the_users_own_list_alone()
    {
        var keep = Row(1, Inv(0), ActionKind.None);
        Assert.Same(keep, ActionPolicyApplier.Apply(keep, ActionPolicy.DiscardAll));

        var mine = Row(1, Inv(1), ActionKind.VendorSell, confidence: Confidence.User);
        // The Always clean list follows the preset too: the list says what is junk, the preset what happens to it.
        var followed = ActionPolicyApplier.Apply(mine, ActionPolicy.DiscardAll);
        Assert.Equal(ActionKind.Discard, followed!.Action);
        Assert.Equal(Confidence.User, followed.Confidence);
    }

    [Fact]
    public void Saddlebag_items_are_only_ever_discarded_whatever_the_preset()
    {
        var row = ActionPolicyApplier.Apply(Row(12, Saddle(0), marketUnit: 400), ActionPolicy.MarketListTradeable)!;
        Assert.Equal(ActionKind.Discard, row.Action);
    }
}

public class ContainerConstraintsTests
{
    [Theory]
    [InlineData(ContainerKind.Inventory, ActionKind.MarketList, true)]
    [InlineData(ContainerKind.Retainer, ActionKind.MarketList, true)]
    [InlineData(ContainerKind.Armoury, ActionKind.MarketList, false)]
    [InlineData(ContainerKind.Retainer, ActionKind.VendorSell, true)]
    [InlineData(ContainerKind.Retainer, ActionKind.Desynth, false)]
    [InlineData(ContainerKind.Saddlebag, ActionKind.VendorSell, false)]
    [InlineData(ContainerKind.Saddlebag, ActionKind.Discard, true)]
    [InlineData(ContainerKind.GlamourDresser, ActionKind.ExpertDelivery, false)]
    public void Allows_action_follows_what_the_game_permits(ContainerKind kind, ActionKind action, bool allowed) =>
        Assert.Equal(allowed, ContainerConstraints.AllowsAction(kind, action));

    [Fact]
    public void Only_expert_delivery_from_a_retainer_needs_a_trip_home()
    {
        Assert.True(ContainerConstraints.NeedsTripHome(ContainerKind.Retainer, ActionKind.ExpertDelivery));
        Assert.False(ContainerConstraints.NeedsTripHome(ContainerKind.Retainer, ActionKind.VendorSell));
        Assert.False(ContainerConstraints.NeedsTripHome(ContainerKind.Retainer, ActionKind.MarketList));
        Assert.False(ContainerConstraints.NeedsTripHome(ContainerKind.Inventory, ActionKind.ExpertDelivery));
    }

    [Fact]
    public void Disallowed_action_becomes_discard_with_a_reason_the_player_can_act_on()
    {
        var p = new Proposal
        {
            Item = ScannedItem.Simple(Saddle(0), 1, 1), Info = Items[1], Action = ActionKind.VendorSell,
            Alternatives = [ActionKind.Discard], Confidence = Confidence.High, RuleId = "test", Reason = "Vendor junk", ValueGil = 28, ValueLabel = "28g",
        };
        var fixedUp = ContainerConstraints.Apply(p);
        Assert.Equal(ActionKind.Discard, fixedUp.Action);
        Assert.Empty(fixedUp.Alternatives);
        Assert.Equal(0, fixedUp.ValueGil);
        Assert.Contains("Bring it to your bags to sell it", fixedUp.Reason);
    }

    [Fact]
    public void Allowed_action_keeps_the_row_but_prunes_impossible_alternatives()
    {
        var p = new Proposal
        {
            Item = ScannedItem.Simple(Ret(0), 4, 1), Info = Items[4], Action = ActionKind.VendorSell,
            Alternatives = [ActionKind.Desynth, ActionKind.Discard], Confidence = Confidence.High, RuleId = "test", Reason = "test",
        };
        var fixedUp = ContainerConstraints.Apply(p);
        Assert.Equal(ActionKind.VendorSell, fixedUp.Action);
        Assert.Equal([ActionKind.Discard], fixedUp.Alternatives);
    }
}

public class ExecutionCoreTests
{
    private sealed record Op(string Name, StepStatus Result, string? Blocked = null, bool Throws = false);

    private static (ExecutionCore.Hooks<Op> Hooks, List<(string Op, string Reason)> Parked, List<(string Op, StepOutcome Outcome)> Reported) Wire()
    {
        var parked = new List<(string, string)>();
        var reported = new List<(string, StepOutcome)>();
        var hooks = new ExecutionCore.Hooks<Op>
        {
            BlockedReason = op => op.Blocked,
            Execute = (op, _) => op.Throws ? throw new InvalidOperationException("boom") : Task.FromResult(new StepOutcome(op.Result, op.Result.ToString().ToLowerInvariant())),
            Park = (op, reason) => parked.Add((op.Name, reason)),
            Report = (op, outcome) => reported.Add((op.Name, outcome)),
            Describe = op => op.Name,
        };
        return (hooks, parked, reported);
    }

    [Fact]
    public async Task Blocked_ops_are_parked_and_reported_as_waiting_without_running()
    {
        var (hooks, parked, reported) = Wire();
        var ops = new[] { new Op("a", StepStatus.Done, Blocked: "open the saddlebag"), new Op("b", StepStatus.Done) };
        var summary = await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 3).RunAsync(ops, hooks, CancellationToken.None);

        Assert.False(summary.Aborted);
        Assert.Equal([("a", "open the saddlebag")], parked);
        Assert.Equal("waiting: open the saddlebag", reported[0].Outcome.Message);
        Assert.Equal(StepStatus.Done, reported[1].Outcome.Status);
    }

    [Fact]
    public async Task Failures_in_a_row_abort_and_report_the_rest_as_not_reached()
    {
        var (hooks, parked, reported) = Wire();
        var ops = new[] { new Op("a", StepStatus.Failed), new Op("b", StepStatus.Failed), new Op("c", StepStatus.Done), new Op("d", StepStatus.Done) };
        var summary = await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 2).RunAsync(ops, hooks, CancellationToken.None);

        Assert.True(summary.Aborted);
        Assert.Contains("2 items failed in a row", summary.AbortReason);
        Assert.Contains("the last was b", summary.AbortReason);
        // Parked work resumes by itself later; work the run never reached must not.
        Assert.Empty(parked);
        var unreached = reported.Where(r => r.Outcome.Status == StepStatus.Cancelled).ToList();
        Assert.Equal(["c", "d"], unreached.Select(r => r.Op));
        Assert.All(unreached, r => Assert.Equal(ExecutionCore.NotReachedFailed, r.Outcome.Message));
    }

    [Fact]
    public async Task A_success_resets_the_failure_streak()
    {
        var (hooks, _, _) = Wire();
        var ops = new[] { new Op("a", StepStatus.Failed), new Op("b", StepStatus.Done), new Op("c", StepStatus.Failed), new Op("d", StepStatus.Done) };
        var summary = await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 2).RunAsync(ops, hooks, CancellationToken.None);
        Assert.False(summary.Aborted);
    }

    [Fact]
    public async Task Exceptions_become_failed_outcomes_with_the_message()
    {
        var (hooks, _, reported) = Wire();
        await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 3).RunAsync([new Op("a", StepStatus.Done, Throws: true)], hooks, CancellationToken.None);
        Assert.Equal(StepStatus.Failed, reported.Single().Outcome.Status);
        Assert.Equal("boom", reported.Single().Outcome.Message);
    }

    [Fact]
    public async Task Pending_outcome_from_a_step_parks_the_op_with_its_message()
    {
        var (hooks, parked, _) = Wire();
        await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 3).RunAsync([new Op("a", StepStatus.Pending)], hooks, CancellationToken.None);
        Assert.Equal([("a", "pending")], parked);
    }

    [Fact]
    public async Task Stopping_before_an_op_leaves_everything_unreached_and_nothing_parked()
    {
        var (hooks, parked, reported) = Wire();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var ops = new[] { new Op("a", StepStatus.Done), new Op("b", StepStatus.Done) };

        await new ExecutionCore(new NoDelay(), TimeSpan.Zero, 3).RunAsync(ops, hooks, cts.Token);

        Assert.Empty(parked);
        Assert.Equal(["a", "b"], reported.Select(r => r.Op));
        Assert.All(reported, r => Assert.Equal(ExecutionCore.NotReachedStopped, r.Outcome.Message));
    }
}

public class HardBlockRegisteredSpareTests
{
    private static readonly ItemInfo Minion = ItemInfo.Test(30, "Wind-up Airship", vendor: 0, marketable: false, untradable: true, unique: true, category: "Minion", stack: 1);
    private static readonly ItemInfo Roll = ItemInfo.Test(31, "Rare Orchestrion Roll", vendor: 0, marketable: false, untradable: true, category: "Orchestrion Roll", stack: 1);

    [Fact]
    public void Unregistered_minion_is_guarded_as_unique_and_untradeable()
    {
        var reason = HardBlocks.Check(ScannedItem.Simple(Inv(0), 30, 1), Minion, Context(registered: new Dictionary<uint, bool> { [30] = false }));
        Assert.Equal(HardBlockReason.UniqueUntradeable, reason);
        Assert.False(HardBlocks.IsImmovable(reason));
    }

    [Fact]
    public void Registered_spare_minion_passes_every_guard()
    {
        var reason = HardBlocks.Check(ScannedItem.Simple(Inv(0), 30, 1), Minion, Context(registered: new Dictionary<uint, bool> { [30] = true }));
        Assert.Equal(HardBlockReason.None, reason);
    }

    [Fact]
    public void Registered_spare_roll_skips_the_never_proposed_and_irreplaceable_guards()
    {
        var unknown = HardBlocks.Check(ScannedItem.Simple(Inv(0), 31, 1), Roll, Context());
        Assert.Equal(HardBlockReason.NeverProposedCategory, unknown);

        var spare = HardBlocks.Check(ScannedItem.Simple(Inv(0), 31, 1), Roll, Context(registered: new Dictionary<uint, bool> { [31] = true }));
        Assert.Equal(HardBlockReason.None, spare);
    }

    [Fact]
    public void Curated_protected_ids_and_ultimate_weapons_are_immovable()
    {
        var token = ItemInfo.Test(21197, "UCOB token", vendor: 0, marketable: false, untradable: true, category: "Miscellany");
        var protectedCtx = new ItemContext { CharacterId = 1, ProtectedItemIds = new HashSet<uint> { 21197 }, Registered = new Dictionary<uint, bool> { [21197] = true } };
        var reason = HardBlocks.Check(ScannedItem.Simple(Inv(0), 21197, 1), token, protectedCtx);
        Assert.Equal(HardBlockReason.Protected, reason);
        Assert.True(HardBlocks.IsImmovable(reason));

        var ultimate = new ItemInfo(40000, "Ultimate Blade", 0, 0, 0, true, true, false, false, 3, 90, 665, 0, "Two-handed Sword", 1, true, false, 1, false, false, false, false, EquipSlot.MainHand, 3);
        Assert.True(HardBlocks.IsUltimateWeapon(ultimate));
        Assert.Equal(HardBlockReason.Protected, HardBlocks.Check(ScannedItem.Simple(Arm(0), 40000, 1), ultimate, Context()));

        var bozjan = ultimate with { ItemId = 33200 };
        Assert.False(HardBlocks.IsUltimateWeapon(bozjan));
        Assert.Equal(HardBlockReason.UniqueUntradeable, HardBlocks.Check(ScannedItem.Simple(Arm(0), 33200, 1), bozjan, Context()));
    }

    [Fact]
    public void Registration_never_overrides_the_games_own_refusal()
    {
        var reason = HardBlocks.Check(ScannedItem.Simple(Inv(0), 10, 1), Items[10], Context(registered: new Dictionary<uint, bool> { [10] = true }));
        Assert.Equal(HardBlockReason.Indisposable, reason);
        Assert.True(HardBlocks.IsImmovable(reason));
    }
}
