using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using TidyUp.Core.Execution;
using TidyUp.Core.Model;

namespace TidyUp.Game;

/// <summary>
/// The plugin's <see cref="IGameActions"/>: every method schedules the native call on the framework
/// thread, arms the matching dialog answer, and waits for the inventory event that proves it happened.
/// Pointer work lives in small static <c>unsafe</c> helpers so the async flow stays safe code.
/// </summary>
public sealed class GameActions : IGameActions
{
    private readonly IFramework framework;
    private readonly IGameInventory inventory;
    private readonly GameInventoryScanner scanner;
    private readonly AddonDriver dialogs;
    private readonly InventoryContextDriver context;
    private readonly ItemDatabase db;
    private readonly Configuration config;
    private readonly IPluginLog log;

    public GameActions(IFramework framework, IGameInventory inventory, GameInventoryScanner scanner, AddonDriver dialogs,
        InventoryContextDriver context, ItemDatabase db, Configuration config, IPluginLog log)
    {
        this.framework = framework;
        this.inventory = inventory;
        this.scanner = scanner;
        this.dialogs = dialogs;
        this.context = context;
        this.db = db;
        this.config = config;
        this.log = log;
    }

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(config.Callbacks.ActionTimeoutMs);

    public bool IsContainerAvailable(ContainerKind kind, ulong ownerId) => kind switch
    {
        ContainerKind.Inventory or ContainerKind.Armoury => true,
        ContainerKind.Saddlebag => GameInventoryScanner.IsSaddlebagLoaded(),
        ContainerKind.Retainer => GameInventoryScanner.IsRetainerOpen(ownerId),
        ContainerKind.GlamourDresser => GameInventoryScanner.IsDresserLoaded() && AddonDriver.IsAddonVisible("MiragePrismPrismBox"),
        _ => false,
    };

    public ScannedItem? ReadSlot(SlotRef slot) => scanner.ReadSlot(slot);

    public int FreeInventorySlots() => GameInventoryScanner.FreeInventorySlots();

    public bool IsActionAvailable(ActionKind action) => action switch
    {
        ActionKind.Discard => true,
        ActionKind.VendorSell => AddonDriver.IsAddonVisible("Shop") || AddonDriver.IsAddonVisible("RetainerSellList")
                                 || AddonDriver.IsAddonVisible("InventoryRetainer") || AddonDriver.IsAddonVisible("InventoryRetainerLarge"),
        ActionKind.ExpertDelivery => AddonDriver.IsAddonVisible("GrandCompanySupplyList"),
        ActionKind.Desynth => true,
        _ => false,
    };

    public string ActionRequirement(ActionKind action) => action switch
    {
        ActionKind.VendorSell => "talk to a vendor or summon a retainer",
        ActionKind.ExpertDelivery => "open Expert Delivery at a Grand Company personnel officer",
        _ => string.Empty,
    };

    // ---------- discard ----------

    public Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, ct, () => Native.Discard(slot),
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, db.Get(itemId)?.Name));

    // ---------- dresser ----------

    public async Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct)
    {
        var added = WaitForEvent<InventoryItemAddedArgs>(
            e => e.Item.BaseItemId == itemId && GameContainerIds.KindOf((uint)e.Item.ContainerType) == ContainerKind.Inventory, ct);
        var sent = await framework.RunOnFrameworkThread(() => Native.RestoreFromDresser(dresserSlot.Slot)).ConfigureAwait(false);
        if (!sent)
        {
            log.Warning("RestorePrismBoxItem({Index}) refused (unique already owned, or no inventory space)", dresserSlot.Slot);
            return null;
        }
        var landed = await added.ConfigureAwait(false);
        if (landed is null) return null;
        return new SlotRef(ContainerKind.Inventory, (uint)landed.Item.ContainerType, (int)landed.Item.InventorySlot);
    }

    // ---------- materia ----------

    public async Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        var changed = WaitForEvent<InventoryItemChangedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var opened = await framework.RunOnFrameworkThread(() => context.Invoke(slot, config.Callbacks.RetrieveMateriaLabel)).ConfigureAwait(false);
        if (!opened) return false;
        var confirmed = await dialogs.ExpectAsync("MateriaRetrieveDialog", config.Callbacks.MateriaRetrieveConfirm, null, Timeout, ct).ConfigureAwait(false);
        if (!confirmed) return false;
        return await changed.ConfigureAwait(false) is not null;
    }

    // ---------- sell ----------

    public Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, ct, () => context.Invoke(slot, config.Callbacks.SellLabel),
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, null), dialogOptional: true);

    // ---------- expert delivery ----------

    public async Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var selected = await framework.RunOnFrameworkThread(() => Native.SelectExpertDelivery(itemId, config.Callbacks.ExpertDeliverySelect)).ConfigureAwait(false);
        if (!selected) return false;
        var confirmed = await dialogs.ExpectAsync("GrandCompanySupplyReward", config.Callbacks.ExpertDeliveryConfirm, null, Timeout, ct).ConfigureAwait(false);
        if (!confirmed) return false;
        return await removed.ConfigureAwait(false) is not null;
    }

    // ---------- desynth ----------

    public Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, ct, () => Native.Desynth(slot),
            expectDialog: ("SalvageDialog", config.Callbacks.SalvageConfirm, null));

    // ---------- plumbing ----------

    private async Task<bool> RunAndAwaitRemoval(SlotRef slot, CancellationToken ct, Func<bool> nativeCall,
        (string Addon, int Callback, string? Expect)? expectDialog, bool dialogOptional = false)
    {
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var changed = WaitForEvent<InventoryItemChangedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot && e.Item.IsEmpty, ct);

        var started = await framework.RunOnFrameworkThread(nativeCall).ConfigureAwait(false);
        if (!started)
        {
            dialogs.Disarm();
            return false;
        }

        if (expectDialog is { } d)
        {
            var answered = await dialogs.ExpectAsync(d.Addon, d.Callback, d.Expect, Timeout, ct).ConfigureAwait(false);
            if (!answered && !dialogOptional)
            {
                log.Warning("{Addon} did not appear or was not answered for {Slot}", d.Addon, slot);
                return false;
            }
        }

        var done = await Task.WhenAny(removed, changed).ConfigureAwait(false);
        if (done == removed) return await removed.ConfigureAwait(false) is not null;
        return await changed.ConfigureAwait(false) is not null;
    }

    /// <summary>Resolves with the first matching inventory event, or null on timeout.</summary>
    private Task<T?> WaitForEvent<T>(Func<T, bool> predicate, CancellationToken ct) where T : InventoryEventArgs
    {
        var tcs = new TaskCompletionSource<T?>(TaskCreationOptions.RunContinuationsAsynchronously);
        IGameInventory.InventoryChangelogDelegate handler = changelog =>
        {
            foreach (var e in changelog)
                if (e is T typed && predicate(typed)) { tcs.TrySetResult(typed); break; }
        };
        inventory.InventoryChanged += handler;
        var wait = Task.Delay(Timeout, ct);
        return Task.WhenAny(tcs.Task, wait).ContinueWith(_ =>
        {
            inventory.InventoryChanged -= handler;
            return tcs.Task.IsCompletedSuccessfully ? tcs.Task.Result : null;
        }, CancellationToken.None);
    }

    /// <summary>All pointer code, framework thread only.</summary>
    private static unsafe class Native
    {
        public static bool Discard(SlotRef slot)
        {
            var item = InventoryContextDriver.SlotPointer(slot);
            if (item == null || item->ItemId == 0) return false;
            var agent = AgentModule.Instance()->GetAgentInventoryContext();
            if (agent == null) return false;
            agent->DiscardItem(item, (InventoryType)slot.ContainerId, slot.Slot, 0);
            return true;
        }

        public static bool RestoreFromDresser(int prismBoxIndex)
        {
            var mm = MirageManager.Instance();
            if (mm == null || !mm->PrismBoxLoaded) return false;
            return mm->RestorePrismBoxItem((uint)prismBoxIndex);
        }

        public static bool SelectExpertDelivery(uint itemId, int selectCallback)
        {
            var agent = AgentModule.Instance()->GetAgentGrandCompanySupply();
            var addon = AddonDriver.GetAddon("GrandCompanySupplyList");
            if (agent == null || addon == null || !addon->IsVisible || agent->ItemArray == null) return false;
            for (var i = 0; i < agent->NumItems; i++)
            {
                var entry = agent->ItemArray[i];
                if (entry.ItemId != itemId || !entry.IsTurnInAvailable) continue;
                var values = stackalloc AtkValue[2];
                values[0].SetInt(selectCallback);
                values[1].SetInt(entry.Position);
                return addon->FireCallback(2, values, false);
            }
            return false;
        }

        public static bool Desynth(SlotRef slot)
        {
            var item = InventoryContextDriver.SlotPointer(slot);
            if (item == null || item->ItemId == 0) return false;
            var agent = AgentSalvage.Instance();
            if (agent == null) return false;
            agent->SalvageItem(item, 0, 0);
            return true;
        }
    }
}
