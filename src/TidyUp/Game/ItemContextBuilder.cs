using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;
using Lumina.Excel.Sheets;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>Resolves the per-character facts the rules need. Runs on the framework thread.</summary>
public sealed unsafe class ItemContextBuilder
{
    private readonly IPlayerState player;
    private readonly IDataManager data;
    private readonly ItemDatabase db;
    private readonly Configuration config;
    private readonly IPluginLog log;

    public ItemContextBuilder(IPlayerState player, IDataManager data, ItemDatabase db, Configuration config, IPluginLog log)
    {
        this.player = player;
        this.data = data;
        this.db = db;
        this.config = config;
        this.log = log;
    }

    public ItemContext Build(IReadOnlyDictionary<uint, MarketPrice>? market = null)
    {
        var (gearsetIds, maxIlvl) = ReadGearsets();
        var plates = GameInventoryScanner.PlateItemIds();
        var cid = player.ContentId;

        if (plates is not null)
        {
            config.LastKnownPlateItems[cid] = plates.ToList();
        }
        else if (config.LastKnownPlateItems.TryGetValue(cid, out var cached) && cached.Count > 0)
        {
            // Plates only load with the dresser open; a cached set from the last dresser visit is still authoritative
            // for "is this item in any plate" because plates cannot change without the dresser being open.
            plates = new HashSet<uint>(cached);
        }

        return new ItemContext
        {
            CharacterId = cid,
            CharacterName = player.CharacterName,
            GearsetItemIds = gearsetIds,
            PlateItemIds = plates ?? new HashSet<uint>(),
            PlatesLoaded = plates is not null,
            JobLevels = ReadJobLevels(),
            ClassJobCategoryJobs = db.ClassJobCategoryJobs(),
            MaxGearsetItemLevel = maxIlvl,
            RecipesUsing = db.RecipesUsing,
            SeasonalItemIds = new HashSet<uint>(db.Curated.SeasonalItemIds),
            ProtectedItemIds = new HashSet<uint>(db.Curated.ProtectedItemIds),
            RetiredCurrencyGearIds = db.RetiredCurrencyGear(),
            MarketPrices = market ?? new Dictionary<uint, MarketPrice>(),
        };
    }

    private (HashSet<uint> Ids, int MaxItemLevel) ReadGearsets()
    {
        var ids = new HashSet<uint>();
        var maxIlvl = 0;
        var gm = RaptureGearsetModule.Instance();
        if (gm == null) return (ids, maxIlvl);
        try
        {
            for (var i = 0; i < gm->Entries.Length; i++)
            {
                if (!gm->IsValidGearset(i)) continue;
                var entry = gm->GetGearset(i);
                if (entry == null) continue;
                if (entry->ItemLevel > maxIlvl) maxIlvl = entry->ItemLevel;
                foreach (ref var item in entry->Items)
                    if (item.ItemId != 0) ids.Add(ScannedItem.BaseItemId(item.ItemId));
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Gearset read failed; treating every equipment item as possibly referenced");
        }
        return (ids, maxIlvl);
    }

    private Dictionary<uint, short> ReadJobLevels()
    {
        var levels = new Dictionary<uint, short>();
        try
        {
            foreach (var job in data.GetExcelSheet<ClassJob>()!)
            {
                if (job.RowId == 0) continue;
                levels[job.RowId] = player.GetClassJobLevel(job);
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Job level read failed");
        }
        return levels;
    }
}
