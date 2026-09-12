using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Gleam.Core.Integrations;
using Gleam.Core.Model;

namespace Gleam.Game;

/// <summary>
/// Lumina → <see cref="ItemInfo"/>, plus the derived indexes the rules need: recipes by ingredient,
/// class job categories, retired-currency gear, and vendor-buyable items. English names are used
/// for category matching regardless of client language; item names follow the client.
/// </summary>
public sealed class ItemDatabase
{
    private readonly IDataManager data;
    private readonly IPluginLog log;
    private readonly ConcurrentDictionary<uint, ItemInfo?> infoCache = new();
    private readonly ExcelSheet<Item> items;
    private readonly ExcelSheet<Item> itemsEn;
    private readonly ExcelSheet<ItemUICategory> uiCategoriesEn;
    private readonly ExcelSheet<Stain> stains;

    // Each index reads a whole sheet. They are built once, under one lock, from whichever thread asks first; two
    // callers used to build the same one side by side.
    private readonly object buildGate = new();
    private volatile Dictionary<uint, List<RecipeUse>>? recipesByIngredient;
    private volatile Dictionary<uint, IReadOnlyList<uint>>? classJobCategoryJobs;
    private volatile HashSet<uint>? vendorBuyable;
    private volatile HashSet<uint>? retiredCurrencyGear;

    public ItemDatabase(IDataManager data, IPluginLog log)
    {
        this.data = data;
        this.log = log;
        items = data.GetExcelSheet<Item>()!;
        itemsEn = data.GetExcelSheet<Item>(ClientLanguage.English)!;
        uiCategoriesEn = data.GetExcelSheet<ItemUICategory>(ClientLanguage.English)!;
        stains = data.GetExcelSheet<Stain>()!;
    }

    public CuratedData Curated { get; set; } = CuratedData.Empty;

    public ItemInfo? Get(uint itemId) => infoCache.GetOrAdd(itemId, Build);

    public string StainName(byte stainId)
    {
        if (stainId == 0) return string.Empty;
        return stains.TryGetRow(stainId, out var s) ? s.Name.ExtractText() : $"dye {stainId}";
    }

    private ItemInfo? Build(uint itemId)
    {
        if (itemId == 0 || !items.TryGetRow(itemId, out var row)) return null;
        var en = itemsEn.TryGetRow(itemId, out var e) ? e : row;

        var uiCatId = row.ItemUICategory.RowId;
        var uiCat = uiCategoriesEn.TryGetRow(uiCatId, out var c) ? c.Name.ExtractText() : string.Empty;
        var isEquipment = row.EquipSlotCategory.RowId != 0;
        var marketable = !row.IsUntradable && row.ItemSearchCategory.RowId != 0;

        return new ItemInfo(
            itemId,
            row.Name.ExtractText(),
            row.Icon,
            row.PriceLow,
            row.PriceMid,
            row.IsUntradable,
            row.IsUnique,
            row.IsIndisposable,
            row.CanBeHq,
            row.Rarity,
            row.LevelEquip,
            (ushort)row.LevelItem.RowId,
            uiCatId,
            uiCat,
            row.ClassJobCategory.RowId,
            isEquipment,
            row.Desynth > 0,
            row.StackSize,
            row.DyeCount > 0,
            marketable,
            VendorBuyable.Contains(itemId),
            row.ItemAction.RowId != 0,
            SlotOf(row.EquipSlotCategory.ValueNullable),
            row.MateriaSlotCount);
    }

    /// <summary>Which body slot an EquipSlotCategory row allows. Two-handed weapons are main-hand; rings share one page.</summary>
    private static EquipSlot SlotOf(EquipSlotCategory? cat)
    {
        if (cat is null) return EquipSlot.None;
        var c = cat.Value;
        if (c.MainHand > 0) return EquipSlot.MainHand;
        if (c.OffHand > 0) return EquipSlot.OffHand;
        if (c.Head > 0) return EquipSlot.Head;
        if (c.Body > 0) return EquipSlot.Body;
        if (c.Gloves > 0) return EquipSlot.Hands;
        if (c.Legs > 0) return EquipSlot.Legs;
        if (c.Feet > 0) return EquipSlot.Feet;
        if (c.Ears > 0) return EquipSlot.Ears;
        if (c.Neck > 0) return EquipSlot.Neck;
        if (c.Wrists > 0) return EquipSlot.Wrists;
        if (c.FingerL > 0 || c.FingerR > 0) return EquipSlot.Ring;
        if (c.SoulCrystal > 0) return EquipSlot.SoulCrystal;
        return EquipSlot.None;
    }

    /// <summary>
    /// Builds the whole-sheet lookups in the background at load. Built on first use they landed on the game's thread
    /// in the middle of the first scan, or the first sale of a session, a stall of up to a second on a slow PC.
    /// </summary>
    public void WarmUp(IEnumerable<string> englishMenuTexts)
    {
        try
        {
            _ = VendorBuyable;
            ClassJobCategoryJobs();
            RetiredCurrencyGear();
            RecipesUsing(0);
            if (!ClientIsEnglish)
            {
                lock (clientIndexGate) addonIdsByClientText ??= BuildClientTextIndex();
                foreach (var text in englishMenuTexts) LocalizeMenuText(text);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Warming up the game data lookups failed; they are built on first use instead");
        }
    }

    /// <summary>Ingredient item id → recipes using it, reduced to (craft job id, required level).</summary>
    public IReadOnlyList<RecipeUse> RecipesUsing(uint itemId)
    {
        if (recipesByIngredient is null) lock (buildGate) recipesByIngredient ??= BuildRecipeIndex();
        return recipesByIngredient.TryGetValue(itemId, out var list) ? list : Array.Empty<RecipeUse>();
    }

    private Dictionary<uint, List<RecipeUse>> BuildRecipeIndex()
    {
        var index = new Dictionary<uint, List<RecipeUse>>();
        try
        {
            foreach (var recipe in data.GetExcelSheet<Recipe>()!)
            {
                if (recipe.ItemResult.RowId == 0) continue;
                // CraftType 0..7 = CRP..CUL, whose ClassJob ids are 8..15.
                var job = 8u + recipe.CraftType.RowId;
                var level = recipe.RecipeLevelTable.ValueNullable?.ClassJobLevel ?? 0;
                var use = new RecipeUse(job, level);
                foreach (var ing in recipe.Ingredient)
                {
                    if (ing.RowId == 0) continue;
                    if (!index.TryGetValue(ing.RowId, out var list)) index[ing.RowId] = list = new List<RecipeUse>();
                    list.Add(use);
                }
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Recipe index failed; the crafting-material rule will stay quiet");
        }
        return index;
    }

    /// <summary>ClassJobCategory id → job ids, discovered by matching the sheet's boolean columns to job abbreviations.</summary>
    public IReadOnlyDictionary<uint, IReadOnlyList<uint>> ClassJobCategoryJobs()
    {
        if (classJobCategoryJobs is not null) return classJobCategoryJobs;
        lock (buildGate) return classJobCategoryJobs ??= BuildClassJobCategoryJobs();
    }

    private Dictionary<uint, IReadOnlyList<uint>> BuildClassJobCategoryJobs()
    {
        var result = new Dictionary<uint, IReadOnlyList<uint>>();
        try
        {
            var jobsByAbbr = data.GetExcelSheet<ClassJob>(ClientLanguage.English)!
                .Where(j => j.RowId != 0)
                .GroupBy(j => j.Abbreviation.ExtractText().ToUpperInvariant())
                .ToDictionary(g => g.Key, g => g.First().RowId);

            var props = typeof(ClassJobCategory).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType == typeof(bool) && jobsByAbbr.ContainsKey(p.Name.ToUpperInvariant()))
                .Select(p => (Prop: p, Job: jobsByAbbr[p.Name.ToUpperInvariant()]))
                .ToList();

            foreach (var cat in data.GetExcelSheet<ClassJobCategory>()!)
            {
                var jobs = new List<uint>();
                object boxed = cat;
                foreach (var (prop, job) in props)
                    if (prop.GetValue(boxed) is true) jobs.Add(job);
                result[cat.RowId] = jobs;
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "ClassJobCategory index failed; gear rules will use the highest job level overall");
        }
        return result;
    }

    public IReadOnlyList<uint> AllJobIds()
    {
        try
        {
            return data.GetExcelSheet<ClassJob>()!.Where(j => j.RowId != 0).Select(j => j.RowId).ToList();
        }
        catch
        {
            return Array.Empty<uint>();
        }
    }

    private HashSet<uint> VendorBuyable
    {
        get
        {
            if (vendorBuyable is not null) return vendorBuyable;
            lock (buildGate) return vendorBuyable ??= BuildVendorBuyable();
        }
    }

    private HashSet<uint> BuildVendorBuyable()
    {
        var set = new HashSet<uint>();
        try
        {
            foreach (var row in data.GetSubrowExcelSheet<GilShopItem>()!)
                foreach (var sub in row)
                    if (sub.Item.RowId != 0) set.Add(sub.Item.RowId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "GilShopItem index failed; reacquire hints will be missing");
        }
        return set;
    }

    /// <summary>Equipment sold in any special shop for a retired tomestone or a curated retired scrip.</summary>
    public IReadOnlySet<uint> RetiredCurrencyGear()
    {
        if (retiredCurrencyGear is not null) return retiredCurrencyGear;
        lock (buildGate) return retiredCurrencyGear ??= BuildRetiredCurrencyGear();
    }

    private HashSet<uint> BuildRetiredCurrencyGear()
    {
        var gear = new HashSet<uint>();
        try
        {
            var current = new HashSet<uint>(data.GetExcelSheet<TomestonesItem>()!.Select(t => t.Item.RowId).Where(i => i != 0));
            var retiredCurrencies = new HashSet<uint>();
            var retiredNames = new HashSet<string>(Curated.RetiredCurrencyNames, StringComparer.OrdinalIgnoreCase);
            foreach (var item in itemsEn)
            {
                if (item.RowId == 0) continue;
                var name = item.Name.ExtractText();
                if (name.StartsWith("Allagan Tomestone of", StringComparison.OrdinalIgnoreCase) && !current.Contains(item.RowId))
                    retiredCurrencies.Add(item.RowId);
                else if (retiredNames.Contains(name))
                    retiredCurrencies.Add(item.RowId);
            }

            foreach (var shop in data.GetExcelSheet<SpecialShop>()!)
            {
                foreach (var entry in shop.Item)
                {
                    var retired = false;
                    foreach (var cost in entry.ItemCosts)
                        if (cost.ItemCost.RowId != 0 && retiredCurrencies.Contains(cost.ItemCost.RowId)) { retired = true; break; }
                    if (!retired) continue;
                    foreach (var recv in entry.ReceiveItems)
                    {
                        var id = recv.Item.RowId;
                        if (id == 0) continue;
                        if (items.TryGetRow(id, out var it) && it.EquipSlotCategory.RowId != 0) gear.Add(id);
                    }
                }
            }
            log.Debug("Retired-currency gear index: {Currencies} currencies, {Gear} gear items", retiredCurrencies.Count, gear.Count);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Retired-currency index failed; that rule will stay quiet");
        }
        return gear;
    }

    /// <summary>Addon sheet row id for an English UI label, used to find context menu entries by meaning rather than index.</summary>
    public uint? AddonRowIdForEnglishText(string text)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<Addon>(ClientLanguage.English)!)
                if (string.Equals(row.Text.ExtractText(), text, StringComparison.OrdinalIgnoreCase)) return row.RowId;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Addon sheet lookup failed for {Text}", text);
        }
        return null;
    }

    private readonly ConcurrentDictionary<uint, bool> vendorNpcCache = new();

    /// <summary>
    /// True when the NPC's event data references a GilShop. Every gil shop buys items, so this finds a
    /// merchant in any language without knowing its name. GilShop row ids live in the 0x40000 block.
    /// </summary>
    public bool IsVendorNpc(uint enpcBaseId)
    {
        if (enpcBaseId == 0) return false;
        return vendorNpcCache.GetOrAdd(enpcBaseId, id =>
        {
            try
            {
                if (!data.GetExcelSheet<ENpcBase>()!.TryGetRow(id, out var npc)) return false;
                foreach (var d in npc.ENpcData)
                    if (d.RowId is >= 0x40000 and < 0x50000) return true;
            }
            catch
            {
                // unreadable row: not a vendor we can trust
            }
            return false;
        });
    }

    /// <summary>MainCommand row id for an English command name (e.g. "Chocobo Saddlebag"), or null.</summary>
    public uint? MainCommandIdForEnglishName(string englishName) => mainCommandIds.GetOrAdd(englishName, name =>
    {
        try
        {
            foreach (var row in data.GetExcelSheet<MainCommand>(ClientLanguage.English)!)
                if (string.Equals(row.Name.ExtractText(), name, StringComparison.OrdinalIgnoreCase)) return row.RowId;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MainCommand lookup failed for {Name}", name);
        }
        return null;
    });

    private readonly ConcurrentDictionary<string, uint?> mainCommandIds = new(StringComparer.OrdinalIgnoreCase);

    // ---------- English -> client language ----------
    // Settings and defaults are written in English. The game's own sheets carry every language, so an
    // English name is looked up in the English sheet and read back from the client's sheet.

    // Written from the game's thread and from runs on the thread pool alike; a plain Dictionary can corrupt itself that way.
    private readonly ConcurrentDictionary<(string Kind, string En), string> localized = new(new TupleComparer());

    private sealed class TupleComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) a, (string, string) b) => a.Item1 == b.Item1 && string.Equals(a.Item2, b.Item2, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string, string) k) => HashCode.Combine(k.Item1, k.Item2.ToLowerInvariant());
    }

    private bool ClientIsEnglish => data.Language == ClientLanguage.English;

    /// <summary>A placed object's name (bell, dresser) in the client language.</summary>
    public string LocalizeObjectName(string english) => Localize("eobj", english, () =>
    {
        var en = data.GetExcelSheet<EObjName>(ClientLanguage.English)!;
        var local = data.GetExcelSheet<EObjName>()!;
        foreach (var row in en)
            if (string.Equals(row.Singular.ExtractText(), english, StringComparison.OrdinalIgnoreCase) && local.TryGetRow(row.RowId, out var l))
                return l.Singular.ExtractText();
        return null;
    });

    /// <summary>An NPC's name in the client language.</summary>
    public string LocalizeNpcName(string english) => Localize("npc", english, () =>
    {
        var en = data.GetExcelSheet<ENpcResident>(ClientLanguage.English)!;
        var local = data.GetExcelSheet<ENpcResident>()!;
        foreach (var row in en)
            if (string.Equals(row.Singular.ExtractText(), english, StringComparison.OrdinalIgnoreCase) && local.TryGetRow(row.RowId, out var l))
                return l.Singular.ExtractText();
        return null;
    });

    /// <summary>An aetheryte or area name in the client language.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlySet<uint>> aetheryteZones = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a zone is the one the named aetheryte stands in. The name is English, as the settings keep it.
    /// A teleport to the zone you are already in costs gil, lands in the same zone, and then waits for a zone
    /// change that never comes; asking this first skips it.
    /// </summary>
    public bool IsZoneOfAetheryte(uint territoryId, string englishAetheryte)
    {
        if (territoryId == 0 || string.IsNullOrWhiteSpace(englishAetheryte)) return false;
        var zones = aetheryteZones.GetOrAdd(englishAetheryte, name =>
        {
            var set = new HashSet<uint>();
            try
            {
                foreach (var row in data.GetExcelSheet<Aetheryte>(ClientLanguage.English)!)
                    if (row.IsAetheryte && row.Territory.RowId != 0
                        && string.Equals(row.PlaceName.ValueNullable?.Name.ExtractText(), name, StringComparison.OrdinalIgnoreCase))
                        set.Add(row.Territory.RowId);
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not find the zone of the aetheryte {Name}", name);
            }
            return set;
        });
        return zones.Contains(territoryId);
    }

    public string LocalizePlaceName(string english) => Localize("place", english, () =>
    {
        var en = data.GetExcelSheet<PlaceName>(ClientLanguage.English)!;
        var local = data.GetExcelSheet<PlaceName>()!;
        foreach (var row in en)
            if (string.Equals(row.Name.ExtractText(), english, StringComparison.OrdinalIgnoreCase) && local.TryGetRow(row.RowId, out var l))
                return l.Name.ExtractText();
        return null;
    });

    /// <summary>
    /// The game text rows the menus Gleam uses are built from, by the English fragment the settings keep. A null sheet
    /// means Addon. Guessing "the shortest Addon row that contains the fragment" picked the wrong row for most of
    /// these (row 772 "Entrust Quantity", row 5 "Quit", a line about flowers), so every other client failed to find them.
    /// </summary>
    private static readonly Dictionary<string, (string? Sheet, uint Row)[]> KnownMenuRows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Entrust"] = [(null, 2378)],
        ["Quit"] = [(null, 2383)],
        ["your inventory"] = [(null, 2380)],
        ["retainer's inventory"] = [(null, 2381)],
        ["supply"] = [("custom/000/ComDefGrandCompanyOfficer_00073", 69)],
    };

    /// <summary>The rows of questions Gleam answers itself. The buyback question lives in two NPC sheets, not in Addon.</summary>
    private static readonly Dictionary<string, (string? Sheet, uint Row)[]> KnownPromptRows = new(StringComparer.OrdinalIgnoreCase)
    {
        ["buyback"] = [("custom/005/CmnDefRetainerBell_00544", 52), ("custom/000/CmnDefRetainerCall_00010", 215)],
    };

    private readonly ConcurrentDictionary<string, IReadOnlyList<string[]>> rowPieces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The words of each known row, in the client's language.</summary>
    private IReadOnlyList<string[]> PiecesFor(Dictionary<string, (string? Sheet, uint Row)[]> table, string key) =>
        rowPieces.GetOrAdd((table == KnownPromptRows ? "prompt:" : "menu:") + key, _ =>
        {
            var list = new List<string[]>();
            if (!table.TryGetValue(key, out var rows)) return list;
            foreach (var (sheet, row) in rows)
                if (RowText(sheet, row) is { Length: > 0 } text && Core.Execution.MenuText.Pieces(text) is { Length: > 0 } pieces)
                    list.Add(pieces);
            return list;
        });

    /// <summary>Column 1 of a row, in the client's language. Addon rows by their typed sheet, NPC text sheets raw.</summary>
    private string? RowText(string? sheet, uint row)
    {
        try
        {
            if (sheet is null) return data.GetExcelSheet<Addon>()!.TryGetRow(row, out var a) ? a.Text.ExtractText() : null;
            var raw = data.GetExcelSheet<RawRow>(name: sheet);
            return raw.TryGetRow(row, out var r) ? r.ReadStringColumn(1).ExtractText() : null;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read row {Row} of {Sheet}", row, sheet ?? "Addon");
            return null;
        }
    }

    /// <summary>
    /// A menu entry, matched loosely: the shortest English Addon text containing the fragment is taken
    /// as the entry, and its client-language text is returned for matching against the live menu.
    /// Entries with a known row show that row's text instead.
    /// </summary>
    public string LocalizeMenuText(string englishFragment) => Localize("menu", englishFragment, () =>
    {
        if (KnownMenuRows.TryGetValue(englishFragment, out var known) && RowText(known[0].Sheet, known[0].Row) is { Length: > 0 } rowText)
            return rowText;
        var en = data.GetExcelSheet<Addon>(ClientLanguage.English)!;
        var local = data.GetExcelSheet<Addon>()!;
        uint? best = null; var bestLen = int.MaxValue;
        foreach (var row in en)
        {
            var t = row.Text.ExtractText();
            if (t.Length < bestLen && t.Contains(englishFragment, StringComparison.OrdinalIgnoreCase)) { best = row.RowId; bestLen = t.Length; }
        }
        return best is { } id && local.TryGetRow(id, out var l) ? l.Text.ExtractText() : null;
    });

    private string Localize(string kind, string english, Func<string?> lookup)
    {
        if (ClientIsEnglish || string.IsNullOrWhiteSpace(english)) return english;
        if (localized.TryGetValue((kind, english), out var hit)) return hit;
        string result;
        try { result = lookup() ?? english; }
        catch (Exception ex) { log.Warning(ex, "Could not localise {Kind} '{Text}'", kind, english); result = english; }
        localized[(kind, english)] = result;
        return result;
    }

    /// <summary>The language the game client runs in.</summary>
    public ClientLanguage Language => data.Language;

    private readonly ConcurrentDictionary<string, IReadOnlyList<uint>> addonIdsByEnglish = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every Addon sheet row whose English text is exactly this. The sheet repeats common words ("Sell",
    /// "Sort") under several ids and a menu may use any of them. Only the first used to be taken: an English
    /// client was rescued by comparing the text, and every other client simply found nothing.
    /// </summary>
    public IReadOnlyList<uint> AddonRowIdsForEnglishText(string text) => addonIdsByEnglish.GetOrAdd(text, t =>
    {
        var ids = new List<uint>();
        try
        {
            foreach (var row in data.GetExcelSheet<Addon>(ClientLanguage.English)!)
                if (string.Equals(row.Text.ExtractText(), t, StringComparison.OrdinalIgnoreCase)) ids.Add(row.RowId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Addon sheet lookup failed for {Text}", t);
        }
        return ids;
    });

    private readonly object clientIndexGate = new();
    private Dictionary<string, List<uint>>? addonIdsByClientText;

    /// <summary>
    /// The English texts a piece of client-language menu text can stand for. Reading a menu entry back into
    /// English recognises it even when the guess at its translation picked another row of the sheet.
    /// </summary>
    public IReadOnlyList<string> EnglishFor(string clientText)
    {
        if (ClientIsEnglish || string.IsNullOrWhiteSpace(clientText)) return [clientText];
        try
        {
            Dictionary<string, List<uint>> index;
            lock (clientIndexGate) index = addonIdsByClientText ??= BuildClientTextIndex();
            if (!index.TryGetValue(clientText.Trim(), out var ids)) return Array.Empty<string>();
            var en = data.GetExcelSheet<Addon>(ClientLanguage.English)!;
            return ids.Select(id => en.TryGetRow(id, out var r) ? r.Text.ExtractText() : null).OfType<string>().Distinct().ToList();
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read '{Text}' back into English", clientText);
            return Array.Empty<string>();
        }
    }

    private Dictionary<string, List<uint>> BuildClientTextIndex()
    {
        var index = new Dictionary<string, List<uint>>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in data.GetExcelSheet<Addon>()!)
        {
            var t = row.Text.ExtractText().Trim();
            if (t.Length == 0) continue;
            if (!index.TryGetValue(t, out var list)) index[t] = list = new List<uint>();
            list.Add(row.RowId);
        }
        return index;
    }

    /// <summary>
    /// How to recognise a menu entry from an English fragment ("Quit", "your inventory"). On an English client
    /// the fragment itself; elsewhere its translation, or any entry whose English original contains it.
    /// </summary>
    public Func<string, bool> MenuMatcher(string englishFragment)
    {
        if (string.IsNullOrWhiteSpace(englishFragment)) return _ => false;
        var pieces = PiecesFor(KnownMenuRows, englishFragment);
        if (ClientIsEnglish)
            return entry => entry.Contains(englishFragment, StringComparison.OrdinalIgnoreCase) || pieces.Any(p => Core.Execution.MenuText.HasPieces(entry, p));
        // A known row decides on its own: the loose guesses below matched the wrong entry for these on other clients.
        if (pieces.Count > 0) return entry => pieces.Any(p => Core.Execution.MenuText.HasPieces(entry, p));
        var local = LocalizeMenuText(englishFragment);
        return entry => entry.Contains(local, StringComparison.OrdinalIgnoreCase)
                        || EnglishFor(entry).Any(en => en.Contains(englishFragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a menu fragment has a translation on this client. Always true in English.</summary>
    public bool CanTranslateMenuText(string englishFragment) =>
        ClientIsEnglish || PiecesFor(KnownMenuRows, englishFragment).Count > 0
        || !string.Equals(LocalizeMenuText(englishFragment), englishFragment, StringComparison.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, IReadOnlySet<uint>> objectIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every placed-object id whose English name is this ("Summoning Bell"). Objects are found by id, in any language:
    /// by name, a Japanese client picked an unused bell row and never found the bell at all.
    /// </summary>
    public IReadOnlySet<uint> ObjectIdsForEnglishName(string english) => objectIds.GetOrAdd(english, name =>
    {
        var set = new HashSet<uint>();
        try
        {
            foreach (var row in data.GetExcelSheet<EObjName>(ClientLanguage.English)!)
                if (string.Equals(row.Singular.ExtractText(), name, StringComparison.OrdinalIgnoreCase)) set.Add(row.RowId);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not find the objects named {Name}", name);
        }
        return set;
    });

    /// <summary>The names of an NPC's own gil shops in the client's language: what its menu offers to open the shop.</summary>
    public IReadOnlyList<string> GilShopNames(uint enpcBaseId)
    {
        var names = new List<string>();
        try
        {
            if (!data.GetExcelSheet<ENpcBase>()!.TryGetRow(enpcBaseId, out var npc)) return names;
            var shops = data.GetExcelSheet<GilShop>()!;
            foreach (var d in npc.ENpcData)
                if (d.RowId is >= 0x40000 and < 0x50000 && shops.TryGetRow(d.RowId, out var shop) && shop.Name.ExtractText() is { Length: > 0 } n)
                    names.Add(n);
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read the shops of NPC {Npc}", enpcBaseId);
        }
        return names;
    }

    private static readonly System.Text.RegularExpressions.Regex GrammarMarks = new(@"\[[^\]]{1,4}\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    // German names mark an adjective whose ending follows the sentence with [a], and some add a plural ending in
    // brackets. Stripped outright, "fünfblättrig[a] Ahornzweig" never matched the "fünfblättrigen" in a prompt.
    private static readonly System.Text.RegularExpressions.Regex AdjectiveMark = new(@"\[a\]", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex BracketedEnding = new(@"(?<=\p{L})\(\p{Ll}{1,3}\)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// The names a confirmation may use for an item, in the client's language: its name, and the singular
    /// and plural forms the game builds sentences from. German and French prompts inflect the name, and the
    /// English plural endings Gleam relied on before do nothing for them.
    /// </summary>
    public IReadOnlyList<string> PromptNames(uint itemId)
    {
        var names = new List<string>();
        try
        {
            if (!items.TryGetRow(itemId, out var row)) return names;
            void Add(string s)
            {
                s = AdjectiveMark.Replace(s, Core.Execution.PromptMatch.InflectedEnding.ToString());
                s = BracketedEnding.Replace(GrammarMarks.Replace(s, string.Empty), string.Empty).Trim();
                if (s.Length > 0 && !names.Contains(s, StringComparer.OrdinalIgnoreCase)) names.Add(s);
            }
            Add(row.Name.ExtractText());
            Add(row.Singular.ExtractText());
            Add(row.Plural.ExtractText());
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not read the names of item {Item}", itemId);
        }
        return names;
    }

    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> promptTextsAbout = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a yes/no prompt is the game's own question about <paramref name="englishFragment"/> ("buyback"),
    /// in any client language. Elsewhere than English the prompt is compared, letters and digits only, with every
    /// game text whose English original mentions the fragment. A question it cannot place is not the one meant.
    /// </summary>
    public bool PromptIsAbout(string prompt, string englishFragment)
    {
        if (string.IsNullOrWhiteSpace(prompt) || string.IsNullOrWhiteSpace(englishFragment)) return false;
        // The question's own rows first, in the client's language. The French buyback question shares no word with
        // any Addon text about buyback, so a French trip stopped at the first retainer that had sold something.
        if (PiecesFor(KnownPromptRows, englishFragment).Any(p => Core.Execution.MenuText.HasPieces(prompt, p))) return true;
        var flat = AddonDriver.Normalize(prompt);
        if (ClientIsEnglish) return flat.Contains(AddonDriver.Normalize(englishFragment), StringComparison.Ordinal);
        var texts = promptTextsAbout.GetOrAdd(englishFragment, fragment =>
        {
            var found = new List<string>();
            try
            {
                var en = data.GetExcelSheet<Addon>(ClientLanguage.English)!;
                var local = data.GetExcelSheet<Addon>()!;
                foreach (var row in en)
                {
                    if (!row.Text.ExtractText().Contains(fragment, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!local.TryGetRow(row.RowId, out var l)) continue;
                    var t = AddonDriver.Normalize(l.Text.ExtractText());
                    if (t.Length >= 6) found.Add(t);
                }
            }
            catch (Exception ex)
            {
                log.Warning(ex, "Could not read the game's texts about {Fragment}", fragment);
            }
            return found;
        });
        return texts.Any(t => flat.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>Text of an Addon sheet row in the client language, or null.</summary>
    public string? AddonText(uint rowId)
    {
        try
        {
            return data.GetExcelSheet<Addon>()!.TryGetRow(rowId, out var row) ? row.Text.ExtractText() : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Case-insensitive substring search over item names, for the list editors.</summary>
    public IEnumerable<ItemInfo> Search(string query, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;
        var n = 0;
        foreach (var row in items)
        {
            if (row.RowId == 0 || row.ItemUICategory.RowId == 0) continue;
            var name = row.Name.ExtractText();
            if (name.Length == 0 || !name.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var info = Get(row.RowId);
            if (info is null) continue;
            yield return info;
            if (++n >= limit) yield break;
        }
    }
}
