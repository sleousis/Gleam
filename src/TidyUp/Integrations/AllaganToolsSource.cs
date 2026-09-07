using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;
using Dalamud.Plugin.Services;
using TidyUp.Core.Integrations;
using TidyUp.Core.Model;

namespace TidyUp.Integrations;

/// <summary>Reads closed containers and alt characters through Allagan Tools' IPC. Degrades to unavailable when it is not installed.</summary>
public sealed class AllaganToolsSource : IOfflineInventorySource
{
    /// <summary>Only equipment carries materia; the cache stores junk in those fields for other items.</summary>
    public Func<uint, bool> CanHoldMateria { get; init; } = _ => true;

    private readonly IDalamudPluginInterface pi;
    private readonly IPluginLog log;
    private readonly ICallGateSubscriber<bool> isInitialized;
    private readonly ICallGateSubscriber<ulong> currentCharacter;
    private readonly ICallGateSubscriber<bool, HashSet<ulong>> charactersOwned;
    private readonly ICallGateSubscriber<ulong, HashSet<ulong[]>> characterItems;

    public bool Enabled { get; set; } = true;

    /// <summary>Retainer id → display name, filled by the plugin from RetainerManager so cached retainer rows get a name.</summary>
    public Func<IReadOnlyDictionary<ulong, string>>? RetainerNames { get; set; }

    public AllaganToolsSource(IDalamudPluginInterface pi, IPluginLog log)
    {
        this.pi = pi;
        this.log = log;
        isInitialized = pi.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
        currentCharacter = pi.GetIpcSubscriber<ulong>("AllaganTools.CurrentCharacter");
        charactersOwned = pi.GetIpcSubscriber<bool, HashSet<ulong>>("AllaganTools.GetCharactersOwnedByActive");
        characterItems = pi.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
    }

    public bool IsInstalled => pi.InstalledPlugins.Any(p => p.InternalName == "InventoryTools" && p.IsLoaded);

    public bool IsAvailable
    {
        get
        {
            if (!Enabled || !IsInstalled) return false;
            try { return isInitialized.InvokeFunc(); }
            catch { return false; }
        }
    }

    public ulong CurrentCharacterId()
    {
        try { return currentCharacter.InvokeFunc(); }
        catch { return 0; }
    }

    public IReadOnlyList<OfflineCharacter> Characters()
    {
        if (!IsAvailable) return Array.Empty<OfflineCharacter>();
        try
        {
            var active = CurrentCharacterId();
            var retainers = RetainerNames?.Invoke() ?? new Dictionary<ulong, string>();
            var result = new List<OfflineCharacter>();
            foreach (var id in charactersOwned.InvokeFunc(true))
            {
                var isRetainer = retainers.ContainsKey(id);
                var name = isRetainer ? retainers[id] : (id == active ? "This character" : $"Character {id:X}");
                result.Add(new OfflineCharacter(id, name, isRetainer, isRetainer ? active : 0));
            }
            return result;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Allagan Tools character list failed");
            return Array.Empty<OfflineCharacter>();
        }
    }

    public IReadOnlyList<ScannedItem> Items(ulong characterOrRetainerId)
    {
        if (!IsAvailable) return Array.Empty<ScannedItem>();
        try
        {
            var retainers = RetainerNames?.Invoke() ?? new Dictionary<ulong, string>();
            var list = new List<ScannedItem>();
            foreach (var rec in characterItems.InvokeFunc(characterOrRetainerId))
            {
                var ownerName = rec.Length > 23 && retainers.TryGetValue(rec[23], out var n) ? n : string.Empty;
                var item = AllaganItemRecord.Parse(rec, ownerName, characterOrRetainerId);
                if (item is null) continue;
                if (item.HasMateria && !CanHoldMateria(item.ItemId)) item = item with { Materia = Array.Empty<ushort>() };
                list.Add(item);
            }
            return list;
        }
        catch (Exception ex)
        {
            log.Debug(ex, "Allagan Tools item read failed for {Id}", characterOrRetainerId);
            return Array.Empty<ScannedItem>();
        }
    }
}
