using System.Text.Json;
using TidyUp.Core.Execution;
using TidyUp.Core.Integrations;
using TidyUp.Core.Logging;
using TidyUp.Core.Merging;
using TidyUp.Core.Model;
using static TidyUp.Core.Tests.TestData;

namespace TidyUp.Core.Tests;

public class StackMergeTests
{
    [Fact]
    public void Merges_split_stacks_within_a_container_kind_and_owner()
    {
        var items = new[]
        {
            ScannedItem.Simple(Inv(0), 15, 60),
            ScannedItem.Simple(Inv(5), 15, 50),
            ScannedItem.Simple(Inv(9, page: 2), 15, 10),
            ScannedItem.Simple(Ret(0), 15, 40),        // different owner: separate group
            ScannedItem.Simple(Inv(3), 15, 5, hq: true), // HQ never merges with NQ
        };
        var moves = StackMergePlanner.Plan(items, Lookup);

        // 60+50+10 = 120 → one full 99 + one 21. Fullest first: 60←39 from 50, then 60(99)…
        Assert.All(moves, m => Assert.Equal(ContainerKind.Inventory, m.To.Kind));
        Assert.DoesNotContain(moves, m => m.From.Kind == ContainerKind.Retainer || m.To.Kind == ContainerKind.Retainer);
        Assert.DoesNotContain(moves, m => m.From == Inv(3) || m.To == Inv(3));
        Assert.Equal(1, StackMergePlanner.SlotsFreed(moves, items));
        Assert.Equal(120, 60 + moves.Where(m => m.To == Inv(0)).Sum(m => m.Quantity) + (50 - moves.Where(m => m.From == Inv(5)).Sum(m => m.Quantity)) + (10 - moves.Where(m => m.From == Inv(9, 2)).Sum(m => m.Quantity)));
    }

    [Fact]
    public void Never_merges_materia_collectables_or_unstackables()
    {
        var items = new[]
        {
            WithMateria(ScannedItem.Simple(Arm(0), 4, 1), 5),
            WithMateria(ScannedItem.Simple(Arm(1), 4, 1), 5),
            ScannedItem.Simple(Inv(0), 7, 1) with { IsCollectable = true },
            ScannedItem.Simple(Inv(1), 7, 1) with { IsCollectable = true },
            ScannedItem.Simple(Arm(2), 6, 1),
            ScannedItem.Simple(Arm(3), 6, 1),
        };
        Assert.Empty(StackMergePlanner.Plan(items, Lookup));
    }
}

public class IntegrationParsingTests
{
    [Fact]
    public void Allagan_record_maps_container_flags_materia_and_retainer_owner()
    {
        var rec = new ulong[25];
        rec[0] = GameContainerIds.RetainerPage1 + 2; rec[1] = 7; rec[2] = 1_000_006; rec[3] = 2; rec[4] = 5000;
        rec[6] = 1; rec[7] = 12; rec[8] = 34; rec[17] = 3; rec[18] = 0; rec[23] = 0xBEEF;

        var item = AllaganItemRecord.Parse(rec, "Retainer B");
        Assert.NotNull(item);
        Assert.Equal(ContainerKind.Retainer, item!.Slot.Kind);
        Assert.Equal(7, item.Slot.Slot);
        Assert.Equal(6u, item.ItemId);
        Assert.True(item.IsHq);
        Assert.Equal(2, item.Quantity);
        Assert.Equal(0xBEEFu, item.Slot.OwnerId);
        Assert.Equal(2, item.MateriaCount);
        Assert.Equal(3, item.Stain0);
        Assert.Equal("Retainer B", item.OwnerName);
    }

    [Fact]
    public void Allagan_record_uses_the_cache_entry_id_when_the_record_names_no_retainer()
    {
        var rec = new ulong[25];
        rec[0] = GameContainerIds.RetainerPage1; rec[1] = 3; rec[2] = 1; rec[3] = 1; rec[23] = 0;
        Assert.Equal(0xBEEFu, AllaganItemRecord.Parse(rec, "", 0xBEEF)!.Slot.OwnerId);
        rec[23] = 0xCAFE;
        Assert.Equal(0xCAFEu, AllaganItemRecord.Parse(rec, "", 0xBEEF)!.Slot.OwnerId);
    }

    [Fact]
    public void Allagan_record_rejects_unknown_containers_and_short_arrays()
    {
        var rec = new ulong[25]; rec[0] = 2000; rec[2] = 1; rec[3] = 1; // Currency container
        Assert.Null(AllaganItemRecord.Parse(rec, ""));
        Assert.Null(AllaganItemRecord.Parse(new ulong[5], ""));
        var empty = new ulong[25]; empty[0] = 0; empty[2] = 0;
        Assert.Null(AllaganItemRecord.Parse(empty, ""));
    }

    [Fact]
    public void Universalis_parses_multi_and_single_item_shapes()
    {
        var multi = JsonDocument.Parse("""{"items":{"5":{"minPriceNQ":120,"minPriceHQ":300},"6":{"minPriceNQ":0}}}""").RootElement;
        var prices = UniversalisClient.ParseResponse(multi, [5, 6], DateTimeOffset.UnixEpoch).ToDictionary(p => p.ItemId);
        Assert.Equal(120, prices[5].MinNq);
        Assert.Equal(300, prices[5].MinHq);
        Assert.Equal(300, prices[5].MinFor(true));
        Assert.Equal(120, prices[5].MinFor(false));
        Assert.Equal(0, prices[6].MinNq);
        Assert.Equal(0, prices[6].MinFor(true)); // no HQ price falls back to NQ, which is 0

        var single = JsonDocument.Parse("""{"itemID":5,"minPriceNQ":77}""").RootElement;
        var one = Assert.Single(UniversalisClient.ParseResponse(single, [5], DateTimeOffset.UnixEpoch));
        Assert.Equal(77, one.MinNq);
    }

    [Fact]
    public void Curated_data_parses_and_tolerates_garbage()
    {
        var d = CuratedData.Parse("""{"version":"1","updatedForPatch":"7.56","seasonalItemIds":[1,2],"retiredCurrencyNames":["Red Crafters' Scrip"]}""");
        Assert.Equal([1u, 2u], d.SeasonalItemIds);
        Assert.Single(d.RetiredCurrencyNames);
        Assert.Empty(CuratedData.Parse("{").SeasonalItemIds);
    }
}

public class RunLogTests
{
    private sealed class MemoryStorage : ITextStorage
    {
        public Dictionary<string, string> Files { get; } = new();
        public bool Exists(string path) => Files.ContainsKey(path);
        public Task<string?> ReadAsync(string path) => Task.FromResult(Files.GetValueOrDefault(path));
        public Task WriteAsync(string path, string contents) { Files[path] = contents; return Task.CompletedTask; }
    }

    [Fact]
    public async Task Appends_json_lines_and_reads_them_back_skipping_corrupt_lines()
    {
        var storage = new MemoryStorage();
        var log = new JsonLinesRunLog(storage, "tidyup-log.jsonl");
        var who = new RunIdentity(1, "A");
        var action = new QueuedAction(Inv(0), 1, 14, false, ActionKind.VendorSell, false, "Allagan Bronze Piece", 392, "vendor-only-junk");

        await log.AppendAsync(RunLogEntry.From(action, who, ActionOutcome.Done));
        await log.AppendAsync(RunLogEntry.From(action with { Quantity = 1 }, who, ActionOutcome.Done));

        var lines = storage.Files["tidyup-log.jsonl"].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.Contains("\"VendorSell\"", lines[0]);

        storage.Files["tidyup-log.jsonl"] += "this is not json\n";
        var fresh = new JsonLinesRunLog(storage, "tidyup-log.jsonl");
        var all = await fresh.ReadAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(14, all[0].Quantity);
        Assert.Equal("Allagan Bronze Piece", all[1].ItemName);
    }
}

public class ItemTagTests
{
    [Fact]
    public void Items_fall_into_one_coarse_type_each()
    {
        Assert.Equal(ItemTag.Gear, ItemTags.Of(ItemInfo.Test(1, "Sword", equipment: true, category: "Two-handed Conjurer's Arm")));
        Assert.Equal(ItemTag.Materia, ItemTags.Of(ItemInfo.Test(2, "Savage Might Materia XII", category: "Materia")));
        Assert.Equal(ItemTag.Crystals, ItemTags.Of(ItemInfo.Test(3, "Fire Shard", category: "Crystal")));
        Assert.Equal(ItemTag.Materials, ItemTags.Of(ItemInfo.Test(4, "Ash Lumber", category: "Lumber")));
        Assert.Equal(ItemTag.Consumables, ItemTags.Of(ItemInfo.Test(5, "Potion", category: "Medicine")));
        Assert.Equal(ItemTag.Housing, ItemTags.Of(ItemInfo.Test(6, "Oak Table", category: "Table")));
        Assert.Equal(ItemTag.Collectibles, ItemTags.Of(ItemInfo.Test(7, "Wind-up Cursor", category: "Minion")));
        Assert.Equal(ItemTag.Other, ItemTags.Of(ItemInfo.Test(8, "Phial of Fantasia", category: "Miscellany")));
    }
}

public class ArmouryPageTests
{
    [Fact]
    public void Every_equip_slot_maps_to_its_armoury_page_and_nothing_else_does()
    {
        Assert.Equal(GameContainerIds.ArmoryMainHand, GameContainerIds.ArmouryPageFor(EquipSlot.MainHand));
        Assert.Equal(GameContainerIds.ArmoryRings, GameContainerIds.ArmouryPageFor(EquipSlot.Ring));
        Assert.Equal(GameContainerIds.ArmorySoulCrystal, GameContainerIds.ArmouryPageFor(EquipSlot.SoulCrystal));
        Assert.Equal(0u, GameContainerIds.ArmouryPageFor(EquipSlot.None));

        foreach (var slot in Enum.GetValues<EquipSlot>().Where(s => s != EquipSlot.None))
            Assert.NotEqual(0u, GameContainerIds.ArmouryPageFor(slot));

        Assert.Equal(GameContainerIds.ArmoryBody, ItemInfo.Test(1, "Coat", equipment: true).ArmouryPage);
        Assert.Equal(GameContainerIds.ArmoryHead, ItemInfo.Test(2, "Hat", equipment: true, slot: EquipSlot.Head).ArmouryPage);
        Assert.Equal(0u, ItemInfo.Test(3, "Potion").ArmouryPage);
    }
}
