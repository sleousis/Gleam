using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>
/// Drives the game's own item context menu (the right-click menu on an inventory slot) by *label*,
/// resolved through the Addon sheet, so "Sell" and "Retrieve Materia" are found by meaning rather
/// than by a position that changes with the situation.
/// </summary>
public sealed unsafe class InventoryContextDriver
{
    private readonly ItemDatabase db;
    private readonly IPluginLog log;
    private readonly Dictionary<string, uint?> labelIds = new(StringComparer.OrdinalIgnoreCase);

    public InventoryContextDriver(ItemDatabase db, IPluginLog log)
    {
        this.db = db;
        this.log = log;
    }

    public uint? LabelId(string englishLabel)
    {
        if (!labelIds.TryGetValue(englishLabel, out var id))
            labelIds[englishLabel] = id = db.AddonRowIdForEnglishText(englishLabel);
        return id;
    }

    /// <summary>
    /// Opens the context menu for the slot, looks for the entry with the given label, and invokes it.
    /// Returns false when the entry is absent (e.g. no shop is open so "Sell" is not offered).
    /// </summary>
    public bool Invoke(SlotRef slot, string englishLabel)
    {
        var labelId = LabelId(englishLabel);
        if (labelId is null)
        {
            log.Warning("No Addon sheet row for label '{Label}'", englishLabel);
            return false;
        }

        var agent = AgentModule.Instance()->GetAgentInventoryContext();
        if (agent == null) return false;

        var type = (InventoryType)slot.ContainerId;
        agent->OpenForItemSlot(type, slot.Slot, 0, 0);

        var count = agent->ContextItemCount;
        var infos = agent->ContextCallbackInfos;
        if (infos == null || count <= 0)
        {
            log.Debug("Context menu for {Slot} has no entries", slot);
            CloseContext(agent);
            return false;
        }

        for (var i = 0; i < Math.Min(count, 32); i++)
        {
            var info = infos[i];
            if (info.LabelId != labelId.Value) continue;
            if (agent->IsContextItemDisabled(i))
            {
                log.Debug("Context entry '{Label}' is disabled for {Slot}", englishLabel, slot);
                CloseContext(agent);
                return false;
            }
            if (info.Handler == null)
            {
                CloseContext(agent);
                return false;
            }
            log.Debug("Invoking context entry '{Label}' (index {Index}) on {Slot}", englishLabel, i, slot);
            info.Handler->HandleCallback((uint)slot.Slot, type, 0, info.CallbackParam);
            return true;
        }

        log.Debug("Context entry '{Label}' not offered for {Slot}", englishLabel, slot);
        CloseContext(agent);
        return false;
    }

    private static void CloseContext(AgentInventoryContext* agent)
    {
        try
        {
            var addon = AddonDriver.GetAddon("ContextMenu");
            if (addon != null && addon->IsVisible) addon->Close(true);
        }
        catch
        {
            // Closing the menu is cosmetic; never let it fail an action.
        }
    }

    public static InventoryItem* SlotPointer(SlotRef slot)
    {
        var im = InventoryManager.Instance();
        return im == null ? null : im->GetInventorySlot((InventoryType)slot.ContainerId, slot.Slot);
    }
}
