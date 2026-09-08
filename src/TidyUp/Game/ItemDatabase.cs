using System.Collections.Concurrent;
using System.Reflection;
using Dalamud.Game;
using Dalamud.Plugin.Services;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using TidyUp.Core.Integrations;
using TidyUp.Core.Model;

namespace TidyUp.Game;

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

    private Dictionary<uint, List<RecipeUse>>? recipesByIngredient;
    private Dictionary<uint, IReadOnlyList<uint>>? classJobCategoryJobs;
    private HashSet<uint>? vendorBuyable;
    private HashSet<uint>? retiredCurrencyGear;

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
            row.ItemAction.RowId != 0);
    }

    /// <summary>Ingredient item id → recipes using it, reduced to (craft job id, required level).</summary>
    public IReadOnlyList<RecipeUse> RecipesUsing(uint itemId)
    {
        recipesByIngredient ??= BuildRecipeIndex();
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
        classJobCategoryJobs = result;
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

    private HashSet<uint> VendorBuyable => vendorBuyable ??= BuildVendorBuyable();

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
        retiredCurrencyGear = gear;
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
    public uint? MainCommandIdForEnglishName(string englishName)
    {
        try
        {
            foreach (var row in data.GetExcelSheet<MainCommand>(ClientLanguage.English)!)
                if (string.Equals(row.Name.ExtractText(), englishName, StringComparison.OrdinalIgnoreCase)) return row.RowId;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "MainCommand lookup failed for {Name}", englishName);
        }
        return null;
    }

    // ---------- English -> client language ----------
    // Settings and defaults are written in English. The game's own sheets carry every language, so an
    // English name is looked up in the English sheet and read back from the client's sheet.

    private readonly Dictionary<(string Kind, string En), string> localized = new(new TupleComparer());

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
    /// A menu entry, matched loosely: the shortest English Addon text containing the fragment is taken
    /// as the entry, and its client-language text is returned for matching against the live menu.
    /// </summary>
    public string LocalizeMenuText(string englishFragment) => Localize("menu", englishFragment, () =>
    {
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
