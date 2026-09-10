using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>One entry of the game's item context menu as the agent describes it.</summary>
public sealed record ContextEntry(int Index, uint LabelId, string Text, bool Disabled);

/// <summary>
/// Drives the game's own item context menu (the right-click menu on an inventory slot) by *label*.
/// Native entries keep their label in the agent's event parameters as an Addon sheet row id (or a
/// string); the menu is then operated through the ContextMenu addon's callback, exactly as a click would.
/// </summary>
public sealed class InventoryContextDriver
{
    /// <summary>Header values that precede the entry labels in the inventory context agent's parameters.</summary>
    private const int DefaultLabelStart = 8;

    private readonly IFramework framework;
    private readonly ItemDatabase db;
    private readonly IPluginLog log;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (HashSet<uint> Ids, HashSet<string> Texts)> labels = new(StringComparer.OrdinalIgnoreCase);

    public InventoryContextDriver(IFramework framework, ItemDatabase db, IPluginLog log)
    {
        this.framework = framework;
        this.db = db;
        this.log = log;
    }

    public string? LastFailure { get; private set; }

    /// <summary>
    /// Every way an English label can appear in a menu: each Addon row that carries that English text, and
    /// each of those rows' text on this client. Entries are matched by id first, which does not depend on
    /// the language at all.
    /// </summary>
    private (HashSet<uint> Ids, HashSet<string> Texts) Label(string englishLabel) => labels.GetOrAdd(englishLabel, en =>
    {
        var ids = db.AddonRowIdsForEnglishText(en).ToHashSet();
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { en };
        foreach (var id in ids)
            if (db.AddonText(id) is { Length: > 0 } t) texts.Add(t);
        return (ids, texts);
    });

    /// <summary>Whether a menu entry is the one an English label names, on any client.</summary>
    public bool Matches(ContextEntry entry, string englishLabel)
    {
        var (ids, texts) = Label(englishLabel);
        return (entry.LabelId != 0 && ids.Contains(entry.LabelId)) || texts.Contains(entry.Text);
    }

    /// <summary>How many sheet rows carry the label, for the self-test. Zero means Gleam cannot recognise it.</summary>
    public int KnownIds(string englishLabel) => Label(englishLabel).Ids.Count;

    /// <summary>The label as the player reads it on this client, for messages.</summary>
    public string Display(string englishLabel) =>
        Label(englishLabel).Texts.FirstOrDefault(t => !string.Equals(t, englishLabel, StringComparison.OrdinalIgnoreCase)) is { } local && db.Language != Dalamud.Game.ClientLanguage.English
            ? local
            : englishLabel;

    /// <summary>Opens the menu for the slot, reads its entries, and closes it again. Framework thread.</summary>
    public unsafe IReadOnlyList<ContextEntry> ReadEntries(SlotRef slot)
    {
        var agent = AgentModule.Instance()->GetAgentInventoryContext();
        if (agent == null) return Array.Empty<ContextEntry>();
        agent->OpenForItemSlot((InventoryType)slot.ContainerId, slot.Slot, 0, 0);
        var entries = ReadOpenEntries(agent);
        CloseMenu();
        return entries;
    }

    private unsafe List<ContextEntry> ReadOpenEntries(AgentInventoryContext* agent)
    {
        var entries = new List<ContextEntry>();
        var count = agent->ContextItemCount;
        var start = agent->ContexItemStartIndex > 0 ? agent->ContexItemStartIndex : DefaultLabelStart;
        var values = agent->EventParams;
        for (var i = 0; i < count && start + i < values.Length; i++)
        {
            var v = values[start + i];
            uint labelId = 0;
            var text = string.Empty;
            switch (v.Type & AtkValueType.TypeMask)
            {
                case AtkValueType.Int: labelId = (uint)v.Int; break;
                case AtkValueType.UInt: labelId = v.UInt; break;
                case AtkValueType.String:
                case AtkValueType.ConstString:
                    text = v.String.HasValue ? v.String.ToString() : string.Empty;
                    break;
            }
            if (labelId != 0 && text.Length == 0) text = db.AddonText(labelId) ?? string.Empty;
            entries.Add(new ContextEntry(i, labelId, text, agent->IsContextItemDisabled(i)));
        }
        return entries;
    }

    /// <summary>
    /// Opens the context menu for the slot, finds the entry with the given English label and selects it
    /// the way a click would. False (with <see cref="LastFailure"/> set) when the entry is not offered.
    /// </summary>
    public async Task<bool> InvokeAsync(SlotRef slot, string englishLabel, CancellationToken ct)
    {
        LastFailure = null;
        var index = await framework.RunOnFrameworkThread(() => OpenAndFind(slot, englishLabel)).ConfigureAwait(false);
        if (index < 0) return false;

        // The ContextMenu addon is created on the following frame; select the entry once it is up.
        for (var attempt = 0; attempt < 10; attempt++)
        {
            await framework.DelayTicks(2, ct).ConfigureAwait(false);
            var selected = await framework.RunOnFrameworkThread(() => SelectEntry(index)).ConfigureAwait(false);
            if (selected) return true;
        }
        LastFailure = "the item's menu did not open";
        await framework.RunOnFrameworkThread(CloseMenu).ConfigureAwait(false);
        return false;
    }

    private unsafe int OpenAndFind(SlotRef slot, string englishLabel)
    {
        var agent = AgentModule.Instance()->GetAgentInventoryContext();
        if (agent == null) { LastFailure = "the item's menu did not open"; return -1; }
        agent->OpenForItemSlot((InventoryType)slot.ContainerId, slot.Slot, 0, 0);
        var entries = ReadOpenEntries(agent);
        if (entries.Count == 0)
        {
            LastFailure = "the item's menu was empty";
            CloseMenu();
            return -1;
        }

        var match = entries.FirstOrDefault(e => Matches(e, englishLabel));
        if (match is null)
        {
            var offered = string.Join(" | ", entries.Select(e => e.Text.Length > 0 ? $"{e.Text} ({e.LabelId})" : e.LabelId.ToString()));
            // Naming the missing menu entry helps nobody. Say what to do instead. Whether a retainer is open is
            // read from the game, not from the word "Retainer" in the menu, which only works in English.
            var atBell = GameInventoryScanner.ActiveRetainer().Id != 0;
            log.Debug("Menu for {Slot} had no '{Label}'. Offered: {Offered}", slot, englishLabel, offered);
            LastFailure = atBell
                ? "the game hides that option while a retainer is open"
                : $"the game did not offer '{Display(englishLabel)}' for it";
            CloseMenu();
            return -1;
        }
        if (match.Disabled)
        {
            LastFailure = $"'{Display(englishLabel)}' is greyed out for this item";
            CloseMenu();
            return -1;
        }
        return match.Index;
    }

    /// <summary>Fires the ContextMenu addon callback that a click on entry <paramref name="index"/> produces.</summary>
    private static unsafe bool SelectEntry(int index)
    {
        var addon = AddonDriver.GetAddon("ContextMenu");
        if (addon == null || !addon->IsVisible) return false;
        var values = stackalloc AtkValue[5];
        values[0].SetInt(0);
        values[1].SetInt(index);
        values[2].SetUInt(0);
        values[3].SetInt(0);
        values[4].SetInt(0);
        return addon->FireCallback(5, values, true);
    }

    private static unsafe void CloseMenu()
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

    public static unsafe InventoryItem* SlotPointer(SlotRef slot)
    {
        var im = InventoryManager.Instance();
        return im == null ? null : im->GetInventorySlot((InventoryType)slot.ContainerId, slot.Slot);
    }
}
