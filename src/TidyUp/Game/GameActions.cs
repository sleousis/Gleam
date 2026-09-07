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

    /// <summary>Why the most recent action returned false, for the spike window and the run log.</summary>
    public string? LastFailure { get; private set; }

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
        // A retainer's *inventory* window offers no Sell entry; only a vendor shop or the retainer's sell list does.
        ActionKind.VendorSell => AddonDriver.IsAddonVisible("Shop"),
        ActionKind.ExpertDelivery => AddonDriver.IsAddonVisible("GrandCompanySupplyList"),
        ActionKind.Desynth => true,
        _ => false,
    };

    public string ActionRequirement(ActionKind action) => action switch
    {
        ActionKind.VendorSell => "talk to a merchant NPC",
        ActionKind.ExpertDelivery => "open Expert Delivery at a Grand Company personnel officer",
        _ => string.Empty,
    };

    // ---------- discard ----------

    public Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            () => framework.RunOnFrameworkThread(() => Native.Discard(slot)),
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, db.Get(itemId)?.Name));

    // ---------- dresser ----------

    public async Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var added = WaitForEvent<InventoryItemAddedArgs>(
            e => e.Item.BaseItemId == itemId && GameContainerIds.KindOf((uint)e.Item.ContainerType) == ContainerKind.Inventory, ct);
        var sent = await framework.RunOnFrameworkThread(() => Native.RestoreFromDresser(dresserSlot.Slot)).ConfigureAwait(false);
        if (!sent)
        {
            LastFailure = "RestorePrismBoxItem refused: dresser not loaded, unique item already owned, or no inventory space";
            log.Warning("{Failure} (index {Index})", LastFailure, dresserSlot.Slot);
            return null;
        }
        var landed = await added.ConfigureAwait(false);
        if (landed is null)
        {
            LastFailure = "Restore was sent but no item arrived in the inventory before the timeout";
            return null;
        }
        return new SlotRef(ContainerKind.Inventory, (uint)landed.Item.ContainerType, (int)landed.Item.InventorySlot);
    }

    // ---------- materia ----------

    public async Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var changed = WaitForEvent<InventoryItemChangedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var dialog = dialogs.ExpectAsync("MateriaRetrieveDialog", config.Callbacks.MateriaRetrieveConfirm, null, Timeout, ct);
        if (dialog.IsCompleted && !dialog.Result) { LastFailure = dialogs.LastRejection; return false; }
        var opened = await context.InvokeAsync(slot, config.Callbacks.RetrieveMateriaLabel, ct).ConfigureAwait(false);
        if (!opened) { dialogs.Disarm(); LastFailure = context.LastFailure; return false; }
        var confirmed = await dialog.ConfigureAwait(false);
        if (!confirmed) { LastFailure = dialogs.LastRejection ?? "MateriaRetrieveDialog did not appear"; return false; }
        if (await changed.ConfigureAwait(false) is null) { LastFailure = "Materia dialog answered but the item did not change"; return false; }
        return true;
    }

    // ---------- sell ----------

    public Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            async () =>
            {
                var ok = await context.InvokeAsync(slot, config.Callbacks.SellLabel, ct).ConfigureAwait(false);
                if (!ok) LastFailure = context.LastFailure;
                return ok;
            },
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, db.Get(itemId)?.Name), dialogOptional: true);

    // ---------- expert delivery ----------

    public async Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var dialog = dialogs.ExpectAsync("GrandCompanySupplyReward", config.Callbacks.ExpertDeliveryConfirm, null, Timeout, ct);
        if (dialog.IsCompleted && !dialog.Result) { LastFailure = dialogs.LastRejection; return false; }
        var selected = await framework.RunOnFrameworkThread(() => Native.SelectExpertDelivery(itemId, config.Callbacks.ExpertDeliverySelect)).ConfigureAwait(false);
        if (!selected) { dialogs.Disarm(); LastFailure = "Item not in the Expert Delivery list, or the list window is not open"; return false; }
        var confirmed = await dialog.ConfigureAwait(false);
        if (!confirmed) { LastFailure = dialogs.LastRejection ?? "GrandCompanySupplyReward did not appear"; return false; }
        if (await removed.ConfigureAwait(false) is null) { LastFailure = "Reward dialog answered but the item was not removed"; return false; }
        return true;
    }

    // ---------- desynth ----------

    public Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            () => framework.RunOnFrameworkThread(() => Native.Desynth(slot)),
            expectDialog: ("SalvageDialog", config.Callbacks.SalvageConfirm, null));

    // ---------- plumbing ----------

    private async Task<bool> RunAndAwaitRemoval(SlotRef slot, uint expectedItemId, CancellationToken ct, Func<Task<bool>> start,
        (string Addon, int Callback, string? Expect)? expectDialog, bool dialogOptional = false)
    {
        LastFailure = null;
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var changed = WaitForEvent<InventoryItemChangedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot && e.Item.IsEmpty, ct);

        // Arm first, then act: the dialog can only be answered if it appears after this point, and a
        // dialog that is already open (the player's own) makes the whole action refuse to start.
        Task<bool>? dialogTask = null;
        if (expectDialog is { } d)
        {
            dialogTask = dialogs.ExpectAsync(d.Addon, d.Callback, d.Expect, Timeout, ct);
            if (dialogTask.IsCompleted && !dialogTask.Result)
            {
                LastFailure = dialogs.LastRejection;
                return false;
            }
        }

        var started = await start().ConfigureAwait(false);
        if (!started)
        {
            dialogs.Disarm();
            LastFailure ??= "The game call could not be started (empty slot or agent unavailable)";
            return false;
        }

        if (dialogTask is not null)
        {
            var answered = await dialogTask.ConfigureAwait(false);
            if (!answered && !dialogOptional)
            {
                LastFailure = dialogs.LastRejection ?? "dialog was not answered";
                log.Warning("{Failure} for {Slot}", LastFailure, slot);
                return false;
            }
        }

        var done = await Task.WhenAny(removed, changed).ConfigureAwait(false);
        var confirmedByEvent = done == removed
            ? await removed.ConfigureAwait(false) is not null
            : await changed.ConfigureAwait(false) is not null;
        if (confirmedByEvent) return true;

        // Events can lag the server round-trip; the slot itself is the ground truth.
        var after = await framework.RunOnFrameworkThread(() => scanner.ReadSlot(slot)).ConfigureAwait(false);
        if (after is null || after.ItemId != expectedItemId) return true;
        LastFailure = "Dialog answered but the slot still holds the item";
        return false;
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
