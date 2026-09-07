using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

namespace TidyUp.Core.Tests;

/// <summary>Small, named fixtures so each test reads like a sentence.</summary>
internal static class TestData
{
    public const uint CjcAll = 1;      // every job
    public const uint CjcBlm = 2;      // BLM only
    public const uint CjcNeverPlayed = 3;
    public const uint JobBlm = 25;
    public const uint JobCrp = 8;
    public const uint JobWvr = 13;

    public static readonly Dictionary<uint, ItemInfo> Items = new()
    {
        [1] = ItemInfo.Test(1, "Allagan Bronze Piece", vendor: 28, marketable: false),
        [2] = ItemInfo.Test(2, "Rarefied Ash Lumber", vendor: 0, marketable: false, untradable: true, category: "Miscellany"),
        [3] = ItemInfo.Test(3, "Grade 6 Dark Matter", vendor: 45, marketable: false, untradable: true, category: "Medicine", ilvl: 90, levelEquip: 50),
        [4] = ItemInfo.Test(4, "Aetherial Cotton Doublet", vendor: 100, marketable: false, equipment: true, levelEquip: 20, ilvl: 20, rarity: 2, cjc: CjcAll, category: "Body"),
        [5] = ItemInfo.Test(5, "Ironworks Cap of Casting", vendor: 500, marketable: false, equipment: true, levelEquip: 50, ilvl: 130, rarity: 3, cjc: CjcBlm, category: "Head"),
        [6] = ItemInfo.Test(6, "Woolen Coat", vendor: 200, marketable: true, equipment: true, levelEquip: 34, ilvl: 34, rarity: 1, cjc: CjcAll, category: "Body"),
        [7] = ItemInfo.Test(7, "Copper Ore", vendor: 1, marketable: false, category: "Stone"),
        [8] = ItemInfo.Test(8, "Bombard Core", vendor: 0, marketable: false, untradable: true, category: "Miscellany"),
        [9] = ItemInfo.Test(9, "Eternity Ring", vendor: 0, marketable: false, untradable: true, unique: true, category: "Ring", equipment: true, cjc: CjcAll),
        [10] = ItemInfo.Test(10, "Company Seal Voucher", vendor: 0, marketable: false, indisposable: true),
        [11] = ItemInfo.Test(11, "Gil", vendor: 0, marketable: false, category: "Currency"),
        [12] = ItemInfo.Test(12, "Expensive Potion", vendor: 50, marketable: true, category: "Medicine", ilvl: 50, levelEquip: 30),
        [13] = ItemInfo.Test(13, "Dragoon Gear Never Played", vendor: 100, marketable: false, equipment: true, levelEquip: 30, ilvl: 30, rarity: 2, cjc: CjcNeverPlayed, category: "Legs"),
        [14] = ItemInfo.Test(14, "Retired Tome Coat", vendor: 300, marketable: false, equipment: true, levelEquip: 60, ilvl: 270, rarity: 3, cjc: CjcAll, category: "Body"),
        [15] = ItemInfo.Test(15, "Stackable Widget", vendor: 5, marketable: true, stack: 99),
        [16] = ItemInfo.Test(16, "Phial of Fantasia", vendor: 0, marketable: false, untradable: true, category: "Miscellany", stack: 1),
        [17] = ItemInfo.Test(17, "Priority Aetheryte Pass", vendor: 100, marketable: false, untradable: true, category: "Miscellany", usable: true),
        [18] = ItemInfo.Test(18, "Cordial", vendor: 30, marketable: true, category: "Medicine", levelEquip: 1, ilvl: 1),
        [19] = ItemInfo.Test(19, "Grade 2 Tincture", vendor: 30, marketable: true, category: "Medicine", levelEquip: 70, ilvl: 300),
    };

    public static ItemInfo? Lookup(uint id) => Items.GetValueOrDefault(id);

    public static ItemContext Context(
        bool platesLoaded = true,
        IEnumerable<uint>? gearsetItems = null,
        IEnumerable<uint>? plateItems = null,
        int maxGearsetIlvl = 700,
        IReadOnlyDictionary<uint, MarketPrice>? market = null,
        IEnumerable<uint>? seasonal = null,
        IEnumerable<uint>? retiredGear = null,
        Func<uint, IReadOnlyList<RecipeUse>>? recipes = null) => new()
    {
        CharacterId = 0xC0FFEE,
        CharacterName = "Test Char",
        GearsetItemIds = new HashSet<uint>(gearsetItems ?? []),
        PlateItemIds = new HashSet<uint>(plateItems ?? []),
        PlatesLoaded = platesLoaded,
        JobLevels = new Dictionary<uint, short>
        {
            [JobBlm] = 100, [JobCrp] = 90, [JobWvr] = 20, [1] = 50, [19] = 100, [40] = 1,
        },
        ClassJobCategoryJobs = new Dictionary<uint, IReadOnlyList<uint>>
        {
            [CjcAll] = new List<uint> { 1, 19, JobBlm, JobCrp, JobWvr, 40 },
            [CjcBlm] = new List<uint> { JobBlm },
            [CjcNeverPlayed] = new List<uint> { 40 },
        },
        MaxGearsetItemLevel = maxGearsetIlvl,
        MarketPrices = market ?? new Dictionary<uint, MarketPrice>(),
        SeasonalItemIds = new HashSet<uint>(seasonal ?? []),
        RetiredCurrencyGearIds = new HashSet<uint>(retiredGear ?? []),
        RecipesUsing = recipes ?? (_ => Array.Empty<RecipeUse>()),
    };

    public static SlotRef Inv(int slot, uint page = 0) => new(ContainerKind.Inventory, page, slot);
    public static SlotRef Arm(int slot) => new(ContainerKind.Armoury, GameContainerIds.ArmoryBody, slot);
    public static SlotRef Saddle(int slot) => new(ContainerKind.Saddlebag, GameContainerIds.SaddleBag1, slot);
    public static SlotRef Ret(int slot, ulong retainer = 0xBEEF) => new(ContainerKind.Retainer, GameContainerIds.RetainerPage1, slot, retainer);

    public static ScannedItem WithMateria(ScannedItem item, params ushort[] materia) =>
        item with { Materia = materia };

    public static Profile MakeProfile(PresetName preset = PresetName.Balanced)
    {
        var p = new Profile();
        p.ApplyPreset(preset);
        return p;
    }
}
