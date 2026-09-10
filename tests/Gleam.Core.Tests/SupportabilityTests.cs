using Gleam.Core.Execution;
using Gleam.Core.Integrations;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;
using Gleam.Core.Organizer.Execution;
using Gleam.Core.Organizer.Solving;
using static Gleam.Core.Tests.TestData;

namespace Gleam.Core.Tests;

/// <summary>What keeps Gleam safe across game patches, and what it leaves behind to diagnose a problem.</summary>
public class SupportabilityTests
{
    private const string Checked = "2026.09.01.0000.0000";
    private const string NextPatch = "2026.10.14.0000.0000";

    [Fact]
    public void The_game_version_Gleam_was_checked_on_runs_hands_free()
    {
        var status = GameVersionGate.Check(Checked, Checked, null);

        Assert.Equal(GameVersionStatus.Checked, status);
        Assert.True(GameVersionGate.AllowsUnattended(status));
    }

    [Fact]
    public void A_new_patch_holds_hands_free_back_until_the_player_says_go()
    {
        var held = GameVersionGate.Check(NextPatch, Checked, null);
        Assert.Equal(GameVersionStatus.Unchecked, held);
        Assert.False(GameVersionGate.AllowsUnattended(held));

        var accepted = GameVersionGate.Check(NextPatch, Checked, NextPatch);
        Assert.Equal(GameVersionStatus.AcceptedByPlayer, accepted);
        Assert.True(GameVersionGate.AllowsUnattended(accepted));
    }

    [Fact]
    public void Going_ahead_on_one_patch_does_not_cover_the_next()
    {
        var status = GameVersionGate.Check("2026.11.02.0000.0000", Checked, NextPatch);

        Assert.Equal(GameVersionStatus.Unchecked, status);
    }

    [Fact]
    public void A_version_that_cannot_be_read_holds_nothing_back()
    {
        var status = GameVersionGate.Check(null, Checked, null);

        Assert.Equal(GameVersionStatus.Unknown, status);
        Assert.True(GameVersionGate.AllowsUnattended(status));
    }

    [Fact]
    public void A_later_round_waits_while_an_earlier_round_waits_somewhere_else()
    {
        var saddle = new StorageId(ContainerKind.Saddlebag);
        var retainer = new StorageId(ContainerKind.Retainer, 0xA);
        var bags = new StorageId(ContainerKind.Inventory);
        var roundOne = new MoveOp(Guid.NewGuid(), ScannedItem.Simple(Ret(0, 0xA), 6, 1), Lookup(6)!, retainer, bags, MoveLeg.RelayOut, 0, 1);
        var roundTwo = new MoveOp(Guid.NewGuid(), ScannedItem.Simple(Saddle(0), 6, 1), Lookup(6)!, saddle, bags, MoveLeg.RelayOut, 0, 2);

        Assert.Empty(PendingMoveGate.ReadyFor([roundOne, roundTwo], ContainerKind.Saddlebag, _ => true));
        Assert.Equal([roundTwo], PendingMoveGate.ReadyFor([roundTwo], ContainerKind.Saddlebag, _ => true));
    }

    [Fact]
    public void Only_the_storage_that_opened_runs_its_moves()
    {
        var saddle = new StorageId(ContainerKind.Saddlebag);
        var retainer = new StorageId(ContainerKind.Retainer, 0xA);
        var bags = new StorageId(ContainerKind.Inventory);
        var toSaddle = new MoveOp(Guid.NewGuid(), ScannedItem.Simple(Inv(0), 6, 1), Lookup(6)!, bags, saddle, MoveLeg.Direct, 0, 1);
        var toRetainer = new MoveOp(Guid.NewGuid(), ScannedItem.Simple(Inv(1), 6, 1), Lookup(6)!, bags, retainer, MoveLeg.Direct, 0, 1);

        var ready = PendingMoveGate.ReadyFor([toSaddle, toRetainer], ContainerKind.Saddlebag, s => s == saddle);

        Assert.Equal([toSaddle], ready);
    }

    [Fact]
    public async Task A_move_records_the_rule_that_asked_for_it()
    {
        var game = new FakeMoveActions();
        var saddle = new StorageId(ContainerKind.Saddlebag);
        game.Open.Add(saddle);
        var item = ScannedItem.Simple(Inv(0), 6, 1);
        game.Slots[item.Slot] = item;
        var op = new MoveOp(Guid.NewGuid(), item, Lookup(6)!, new StorageId(ContainerKind.Inventory), saddle, MoveLeg.Direct, 0, 1, "Materia to the saddlebag");
        var log = new MemoryMoveLog();

        await new MoveExecutor(game, log, new NoDelay()).ExecuteAsync([op], new RunIdentity(1, "Someone"), CancellationToken.None);

        Assert.Equal("Materia to the saddlebag", Assert.Single(log.Entries).RuleName);
    }

    [Fact]
    public async Task History_adds_one_line_per_entry_instead_of_rewriting_the_file()
    {
        var storage = new AppendingStorage();
        var log = new JsonLinesRunLog(storage, "history.jsonl");

        for (var i = 1; i <= 3; i++) await log.AppendAsync(Entry(i));

        Assert.Equal(0, storage.Writes);
        Assert.Equal(3, storage.Appends);
        Assert.Equal(3, (await new JsonLinesRunLog(storage, "history.jsonl").ReadAllAsync()).Count);
    }

    [Fact]
    public async Task A_line_cut_short_by_a_crash_does_not_swallow_the_next_entry()
    {
        var storage = new AppendingStorage();
        await new JsonLinesRunLog(storage, "history.jsonl").AppendAsync(Entry(1));
        storage.Text += "{\"At\":\"2026-09";   // the game closed half-way through a line

        await new JsonLinesRunLog(storage, "history.jsonl").AppendAsync(Entry(2));
        var all = await new JsonLinesRunLog(storage, "history.jsonl").ReadAllAsync();

        Assert.Equal([1, 2], all.Select(e => e.Quantity));
    }

    private static RunLogEntry Entry(int quantity) =>
        RunLogEntry.From(new QueuedAction(Inv(0), 1, quantity, false, ActionKind.Discard, false, "Thing", 0, "rule"),
            new RunIdentity(1, "Someone"), ActionOutcome.Done);

    private sealed class AppendingStorage : ITextStorage
    {
        public string? Text;
        public int Writes;
        public int Appends;
        public bool Exists(string path) => Text is not null;
        public Task<string?> ReadAsync(string path) => Task.FromResult(Text);
        public Task WriteAsync(string path, string contents) { Writes++; Text = contents; return Task.CompletedTask; }
        public Task AppendAsync(string path, string text) { Appends++; Text = (Text ?? string.Empty) + text; return Task.CompletedTask; }
    }
}
