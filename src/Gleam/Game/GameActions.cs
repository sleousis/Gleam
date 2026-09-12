using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Gleam.Core.Execution;
using Gleam.Core.Model;

namespace Gleam.Game;

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
    private readonly ICondition condition;

    public GameActions(IFramework framework, IGameInventory inventory, GameInventoryScanner scanner, AddonDriver dialogs,
        InventoryContextDriver context, ItemDatabase db, Configuration config, IPluginLog log, ICondition condition)
    {
        this.condition = condition;
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

    /// <summary>A piece was turned in, and the seals the delivery list offered for it.</summary>
    public event Action<uint, int>? SealsEarned;

    private TimeSpan Timeout => TimeSpan.FromMilliseconds(config.Callbacks.ActionTimeoutMs);

    /// <summary>
    /// Reads game memory on the framework thread. The engine awaits with ConfigureAwait(false), so its checks
    /// used to run on the thread pool and race the game rewriting the same slots and windows.
    /// </summary>
    private T OnGame<T>(Func<T> read) =>
        framework.IsInFrameworkUpdateThread || Dalamud.Utility.ThreadSafety.IsMainThread ? read() : framework.RunOnFrameworkThread(read).GetAwaiter().GetResult();

    private ScannedItem? Read(SlotRef slot) => OnGame(() => scanner.ReadSlot(slot));

    private bool Visible(string addon) => OnGame(() => AddonDriver.IsAddonVisible(addon));

    private static string Who(string retainerName) => string.IsNullOrEmpty(retainerName) ? "your retainer" : retainerName;

    public bool IsContainerAvailable(ContainerKind kind, ulong ownerId) => OnGame(() => kind switch
    {
        ContainerKind.Inventory or ContainerKind.Armoury => true,
        ContainerKind.Saddlebag => GameInventoryScanner.IsSaddlebagLoaded() && AddonDriver.IsAddonVisible("InventoryBuddy"),
        ContainerKind.Retainer => GameInventoryScanner.IsRetainerOpen(ownerId),
        ContainerKind.GlamourDresser => GameInventoryScanner.IsDresserLoaded() && AddonDriver.IsAddonVisible("MiragePrismPrismBox"),
        _ => false,
    });

    public ScannedItem? ReadSlot(SlotRef slot) => Read(slot);

    public int FreeInventorySlots() => OnGame(GameInventoryScanner.FreeInventorySlots);

    public bool IsActionAvailable(ActionKind action) => OnGame(() => action switch
    {
        ActionKind.Discard => true,
        // A merchant's shop, or a retainer's inventory: retainers buy at the vendor price too.
        ActionKind.VendorSell => AddonDriver.IsAddonVisible("Shop") || RetainerInventoryOpen,
        ActionKind.ExpertDelivery => AddonDriver.IsAddonVisible("GrandCompanySupplyList"),
        ActionKind.Desynth => true,
        ActionKind.MarketList => AddonDriver.IsAddonVisible("RetainerSellList"),
        _ => false,
    });

    public string ActionRequirement(ActionKind action) => action switch
    {
        ActionKind.VendorSell => "sell to a merchant or through a retainer",
        ActionKind.ExpertDelivery => "turn in at your Grand Company",
        ActionKind.MarketList => "list from a retainer's sell menu",
        _ => string.Empty,
    };

    public const int MarketSlotsPerRetainer = 20;

    public int FreeMarketSlots() => OnGame(Native.FreeMarketSlots);

    // ---------- sort ----------

    /// <summary>
    /// Sorts a container the way the game's own item menu does: pick any item in it and choose "Sort".
    /// The armoury chest is one sub-container per slot type, so each page with an item gets its own pass.
    /// </summary>
    public async Task<int> SortContainerAsync(ContainerKind kind, CancellationToken ct)
    {
        var items = OnGame(() => scanner.ScanKind(kind));
        var pages = items.GroupBy(i => i.Slot.ContainerId).Select(g => g.First().Slot).ToList();
        var sorted = 0;
        foreach (var slot in pages)
        {
            ct.ThrowIfCancellationRequested();
            var ok = await context.InvokeAsync(slot, config.Callbacks.SortLabel, ct).ConfigureAwait(false);
            if (ok) sorted++;
            else log.Debug("Sort not offered for {Slot}: {Why}", slot, context.LastFailure ?? "no reason");
            await Task.Delay(250, ct).ConfigureAwait(false);
        }
        return sorted;
    }

    // ---------- market board ----------

    public async Task<bool> MarketListAsync(SlotRef slot, uint itemId, long unitPrice, int quantity, CancellationToken ct)
    {
        LastFailure = null;
        if (unitPrice <= 0) { LastFailure = "no market price known"; return false; }
        if (!Visible("RetainerSellList")) { LastFailure = "the retainer's sell list is not open"; return false; }
        var before = Read(slot)?.Quantity ?? quantity;
        var listedBefore = await framework.RunOnFrameworkThread(Native.OccupiedMarketSlots).ConfigureAwait(false);

        var opened = await context.InvokeAsync(slot, config.Callbacks.PutUpForSaleLabel, ct).ConfigureAwait(false);
        if (!opened) { LastFailure = context.LastFailure; return false; }

        var deadline = DateTime.UtcNow + Timeout;
        while (!Visible("RetainerSell"))
        {
            if (DateTime.UtcNow > deadline) { LastFailure = "the sell window did not open"; return false; }
            await Task.Delay(50, ct).ConfigureAwait(false);
        }
        await Task.Delay(200, ct).ConfigureAwait(false);

        var price = (int)Math.Clamp(unitPrice, 1, 999_999_999);
        var filled = await framework.RunOnFrameworkThread(() => Native.FillRetainerSell(price, quantity, config.Callbacks.RetainerSellConfirm)).ConfigureAwait(false);
        if (!filled) { LastFailure = "the price could not be entered in the sell window"; await framework.RunOnFrameworkThread(() => AddonDriver.CloseAddon("RetainerSell")).ConfigureAwait(false); return false; }

        // A listing shows up as a *move* into the retainer's market container rather than a removal, so
        // instead of waiting on a particular inventory event, watch the slot itself empty out.
        deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            var live = Read(slot);
            if (live is null || live.ItemId != itemId || live.Quantity <= before - quantity)
                return await ListedAtAsync(itemId, price, listedBefore, ct).ConfigureAwait(false);
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        LastFailure = "the listing was confirmed but the item never left the bag";
        await framework.RunOnFrameworkThread(() => AddonDriver.CloseAddon("RetainerSell")).ConfigureAwait(false);
        return false;
    }

    /// <summary>
    /// Reads the new listing back and compares its price with the one typed in. The sell window takes the
    /// price as keystrokes would, and nothing ever looked at what the market board actually shows. A listing
    /// at the wrong price is reported as failed, which stops the run from listing anything else the same way.
    /// A listing that cannot be read back is let through: the item did go up for sale.
    /// </summary>
    private async Task<bool> ListedAtAsync(uint itemId, int expected, IReadOnlySet<int> listedBefore, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var listing = await framework.RunOnFrameworkThread(() => Native.NewListing(listedBefore, itemId)).ConfigureAwait(false);
            if (listing is { Price: > 0 } l)
            {
                if (l.Price == (ulong)expected) return true;
                LastFailure = $"it went up for sale at {l.Price:N0} gil instead of {expected:N0}. Check the retainer's sell list";
                log.Warning("Listing of {Item} shows {Actual} gil, expected {Expected}", itemId, l.Price, expected);
                return false;
            }
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
        log.Debug("Listing of {Item} could not be read back; its price was not checked", itemId);
        return true;
    }

    /// <summary>
    /// The names a confirmation may use for the item, in the client's language. Null only for an item the
    /// game data does not know, which is never proposed.
    /// </summary>
    private IReadOnlyList<string>? Names(uint itemId) => db.PromptNames(itemId) is { Count: > 0 } names ? names : null;

    // ---------- discard ----------

    public Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            () => framework.RunOnFrameworkThread(() => Native.Discard(slot)),
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, Names(itemId)));

    // ---------- dresser ----------

    public async Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var added = WaitForEvent<InventoryItemAddedArgs>(
            e => e.Item.BaseItemId == itemId && GameContainerIds.KindOf((uint)e.Item.ContainerType) == ContainerKind.Inventory, ct);
        var sent = await framework.RunOnFrameworkThread(() => Native.RestoreFromDresser(dresserSlot.Slot)).ConfigureAwait(false);
        if (!sent)
        {
            LastFailure = "the dresser would not return it: no bag space, or you already own this unique item";
            log.Debug("{Failure} (index {Index})", LastFailure, dresserSlot.Slot);
            return null;
        }
        var landed = await added.ConfigureAwait(false);
        if (landed is null)
        {
            LastFailure = "the dresser was asked to return it but nothing arrived in your bags";
            return null;
        }
        return new SlotRef(ContainerKind.Inventory, (uint)landed.Item.ContainerType, (int)landed.Item.InventorySlot);
    }

    // ---------- materia ----------

    // The item menu drops "Retrieve Materia" for as long as a retainer is summoned, even for items on the character.
    public bool CanRetrieveMateriaIn(ContainerKind kind) =>
        kind is ContainerKind.Inventory or ContainerKind.Armoury && !condition[ConditionFlag.OccupiedSummoningBell];

    public async Task<SlotRef?> MoveToInventoryAsync(SlotRef slot, uint itemId, int quantity, bool isHq, CancellationToken ct)
    {
        LastFailure = null;
        if (slot.Kind != ContainerKind.Retainer) { LastFailure = "only items held by a retainer can be brought back"; return null; }

        // The game drops retrieved gear into the armoury chest when it can; anything else lands in the bags.
        var arrived = WaitForEvent<InventoryEventArgs>(
            e => e is InventoryItemAddedArgs or InventoryItemMovedArgs
                 && e.Item.BaseItemId == itemId
                 && GameContainerIds.KindOf((uint)e.Item.ContainerType) is ContainerKind.Inventory or ContainerKind.Armoury, ct);
        var ok = await context.InvokeAsync(slot, config.Callbacks.RetrieveFromRetainerLabel, ct).ConfigureAwait(false);
        if (!ok) { LastFailure = context.LastFailure; return null; }

        var landed = await arrived.ConfigureAwait(false);
        if (landed is not null)
        {
            var kind = GameContainerIds.KindOf((uint)landed.Item.ContainerType) ?? ContainerKind.Inventory;
            return new SlotRef(kind, (uint)landed.Item.ContainerType, (int)landed.Item.InventorySlot);
        }

        // No event seen: look on the character directly before giving up.
        var found = FindSlot(ContainerKind.Inventory, 0, itemId, quantity, isHq, new HashSet<SlotRef>(), slot)
                    ?? FindSlot(ContainerKind.Armoury, 0, itemId, quantity, isHq, new HashSet<SlotRef>(), slot);
        if (found is null) LastFailure = "the retainer was asked to hand it over but nothing arrived in your bags or armoury";
        return found;
    }

    /// <summary>
    /// The game takes one materia per "Retrieve Materia" request, plays a short animation, and (since
    /// retrieval became guaranteed) shows no confirmation dialog. So: request, wait for the slot to change
    /// or a dialog to show up, wait for the character to be free again, repeat until the item is bare.
    /// </summary>
    public async Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var start = Read(slot);
        if (start is null || start.ItemId != itemId) { LastFailure = "the item is no longer where it was"; return false; }
        var rounds = start.MateriaCount + 1;

        for (var round = 0; round < rounds; round++)
        {
            var current = Read(slot);
            if (current is null || current.ItemId != itemId) { LastFailure = "the item moved while its materia was being removed"; return false; }
            if (!current.HasMateria) return true;

            await WaitUntilFreeAsync(ct).ConfigureAwait(false);

            var changed = WaitForEvent<InventoryItemChangedArgs>(
                e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
            var dialog = dialogs.ExpectAsync("MateriaRetrieveDialog", config.Callbacks.MateriaRetrieveConfirm, null, Timeout, ct);
            if (dialog.IsCompleted && !dialog.Result) { LastFailure = dialogs.LastRejection; return false; }

            var opened = await context.InvokeAsync(slot, config.Callbacks.RetrieveMateriaLabel, ct).ConfigureAwait(false);
            if (!opened) { dialogs.Disarm(); LastFailure = context.LastFailure; return false; }

            var winner = await Task.WhenAny(changed, dialog).ConfigureAwait(false);
            if (winner == dialog)
            {
                if (!dialog.Result) { LastFailure = dialogs.LastRejection ?? "the materia window was not answered"; return false; }
                if (await changed.ConfigureAwait(false) is null) { LastFailure = "the materia window was confirmed but no materia came off"; return false; }
            }
            else
            {
                dialogs.Disarm();
                if (changed.Result is null) { LastFailure = "'Retrieve Materia' was chosen but no materia came off"; return false; }
            }

            // The retrieval animation blocks the next context menu; let it finish.
            await WaitUntilFreeAsync(ct).ConfigureAwait(false);
            await Task.Delay(300, ct).ConfigureAwait(false);
        }

        var after = Read(slot);
        if (after is { HasMateria: true }) { LastFailure = $"{after.MateriaCount} materia still attached after {rounds} tries"; return false; }
        return true;
    }

    /// <summary>Waits (bounded) until the character is not in an occupied/casting state. Never fails; just stops waiting.</summary>
    private async Task WaitUntilFreeAsync(CancellationToken ct)
    {
        for (var i = 0; i < 60; i++)
        {
            if (!IsBusy()) return;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }
    }

    private bool IsBusy() =>
        condition[ConditionFlag.Occupied] || condition[ConditionFlag.Occupied30] || condition[ConditionFlag.Occupied33] ||
        condition[ConditionFlag.Occupied38] || condition[ConditionFlag.Occupied39] || condition[ConditionFlag.OccupiedInEvent] ||
        condition[ConditionFlag.Casting];

    // ---------- sell ----------

    private static bool RetainerInventoryOpen =>
        (AddonDriver.IsAddonVisible("InventoryRetainer") || AddonDriver.IsAddonVisible("InventoryRetainerLarge")) && GameInventoryScanner.ActiveRetainer().Id != 0;

    /// <summary>
    /// Sells for the vendor price. At a merchant's shop that is the shop's Sell entry. With a retainer's
    /// inventory open, the retainer buys instead: its own items directly, bag items after being entrusted.
    /// </summary>
    public Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        if (slot.Kind == ContainerKind.Retainer) return RetainerBuysAsync(slot, itemId, ct);
        if (OnGame(() => !AddonDriver.IsAddonVisible("Shop") && RetainerInventoryOpen)) return EntrustThenRetainerBuysAsync(slot, itemId, ct);
        return RunAndAwaitRemoval(slot, itemId, ct,
            async () =>
            {
                var ok = await context.InvokeAsync(slot, config.Callbacks.SellLabel, ct).ConfigureAwait(false);
                if (!ok) LastFailure = context.LastFailure;
                return ok;
            },
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, Names(itemId)), dialogOptional: true);
    }

    private Task<bool> RetainerBuysAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            async () =>
            {
                var ok = await context.InvokeAsync(slot, config.Callbacks.RetainerSellItemLabel, ct).ConfigureAwait(false);
                if (!ok) LastFailure = context.LastFailure;
                return ok;
            },
            expectDialog: ("SelectYesno", config.Callbacks.YesNoConfirm, Names(itemId)), dialogOptional: true);

    private async Task<bool> EntrustThenRetainerBuysAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        var before = Read(slot);
        if (before is null || before.ItemId != itemId) { LastFailure = "the item is no longer where it was"; return false; }
        var (retainer, retainerName) = OnGame(GameInventoryScanner.ActiveRetainer);
        // Copies of this item the retainer already holds. The one to sell is the copy that arrives: a same-looking
        // copy the retainer had before (with materia, or kept on purpose) used to be found first and sold instead.
        var alreadyThere = OnGame(() => scanner.ScanKind(ContainerKind.Retainer).Where(i => i.ItemId == itemId).Select(i => i.Slot).ToHashSet());

        var handedOver = await RunAndAwaitRemoval(slot, itemId, ct,
            async () =>
            {
                var ok = await context.InvokeAsync(slot, config.Callbacks.EntrustLabel, ct).ConfigureAwait(false);
                if (!ok) LastFailure = context.LastFailure;
                return ok;
            },
            expectDialog: null).ConfigureAwait(false);
        if (!handedOver) { LastFailure = $"the retainer did not take the item ({LastFailure ?? "no reason given"})"; return false; }

        // Find where it landed with the retainer, then have the retainer sell it. The item is with the retainer
        // now and Stop cannot put it back in the bag, so the sale finishes on its own timeouts. A Stop here used
        // to leave the item with the retainer and report it as left untouched.
        var finish = CancellationToken.None;
        SlotRef? landed = null;
        for (var i = 0; i < 20 && landed is null; i++)
        {
            landed = FindSlot(ContainerKind.Retainer, retainer, itemId, before.Quantity, before.IsHq, alreadyThere, slot);
            if (landed is null) await Task.Delay(100, finish).ConfigureAwait(false);
        }
        if (landed is null) { LastFailure = $"the item went to {Who(retainerName)} but could not be found there to sell. It is with that retainer now"; return false; }
        if (Read(landed.Value) is { HasMateria: true })
        {
            LastFailure = $"the copy with {Who(retainerName)} carries materia, so it was not sold. It is with that retainer now";
            return false;
        }
        await Task.Delay(config.Callbacks.RateLimitMs, finish).ConfigureAwait(false);
        var sold = await RetainerBuysAsync(landed.Value, itemId, finish).ConfigureAwait(false);
        if (!sold) LastFailure = $"{LastFailure ?? "the sale did not go through"}. The item is with {Who(retainerName)} now";
        return sold;
    }

    // ---------- expert delivery ----------

    public async Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct)
    {
        LastFailure = null;
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, ct);
        var dialog = dialogs.ExpectAsync("GrandCompanySupplyReward", config.Callbacks.ExpertDeliveryConfirm, null, Timeout, ct);
        if (dialog.IsCompleted && !dialog.Result) { LastFailure = dialogs.LastRejection; return false; }
        var selected = await framework.RunOnFrameworkThread(() => Native.SelectExpertDelivery(slot, itemId, config.Callbacks.ExpertDeliverySelect)).ConfigureAwait(false);
        if (selected != Native.DeliveryPick.Selected)
        {
            dialogs.Disarm();
            LastFailure = selected == Native.DeliveryPick.OtherCopyOnly
                ? "the list only offers another copy of it, so nothing was turned in"
                : "the item is not on the Expert Delivery list";
            return false;
        }
        var confirmed = await dialog.ConfigureAwait(false);
        if (!confirmed) { LastFailure = dialogs.LastRejection ?? "the delivery confirmation did not open"; return false; }
        if (await removed.ConfigureAwait(false) is null) { LastFailure = "the delivery was confirmed but the item stayed in your bags"; return false; }
        SealsEarned?.Invoke(itemId, Native.LastSealReward);
        return true;
    }

    // ---------- desynth ----------

    public Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct) =>
        RunAndAwaitRemoval(slot, itemId, ct,
            () => framework.RunOnFrameworkThread(() => Native.Desynth(slot)),
            expectDialog: ("SalvageDialog", config.Callbacks.SalvageConfirm, null));

    // ---------- plumbing ----------

    private async Task<bool> RunAndAwaitRemoval(SlotRef slot, uint expectedItemId, CancellationToken ct, Func<Task<bool>> start,
        (string Addon, int Callback, IReadOnlyList<string>? Expect)? expectDialog, bool dialogOptional = false)
    {
        LastFailure = null;
        // Once the request may reach the server the item is gone or it is not, and Stop cannot change that.
        // So the confirmation is waited out on its own timeouts rather than on the run's token: an item
        // destroyed mid-Stop used to be reported as cancelled and left out of history.
        var finish = CancellationToken.None;
        var removed = WaitForEvent<InventoryItemRemovedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot, finish);
        var changed = WaitForEvent<InventoryItemChangedArgs>(
            e => (uint)e.Item.ContainerType == slot.ContainerId && e.Item.InventorySlot == (uint)slot.Slot && e.Item.IsEmpty, finish);

        // Arm first, then act: the dialog can only be answered if it appears after this point, and a
        // dialog that is already open (the player's own) makes the whole action refuse to start.
        Task<bool>? dialogTask = null;
        if (expectDialog is { } d)
        {
            dialogTask = dialogs.ExpectAsync(d.Addon, d.Callback, d.Expect, Timeout, finish);
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
            LastFailure ??= "the game did not accept the request";
            return false;
        }

        if (dialogTask is not null)
        {
            if (dialogOptional)
            {
                // Common sales show no dialog at all: whichever comes first wins, the dialog or the item leaving.
                var first = await Task.WhenAny(dialogTask, removed, changed).ConfigureAwait(false);
                if (first != dialogTask) dialogs.Disarm();
            }
            else
            {
                var answered = await dialogTask.ConfigureAwait(false);
                if (!answered)
                {
                    LastFailure = dialogs.LastRejection ?? "the confirmation was not answered";
                    log.Debug("{Failure} for {Slot}", LastFailure, slot);
                    return false;
                }
            }
        }

        var done = await Task.WhenAny(removed, changed).ConfigureAwait(false);
        var confirmedByEvent = done == removed
            ? await removed.ConfigureAwait(false) is not null
            : await changed.ConfigureAwait(false) is not null;
        if (confirmedByEvent) return true;

        // Events can lag the server round-trip; the slot itself is the ground truth. Check twice, two seconds
        // apart, and only trust an *emptied* slot in a *loaded* container. An unloaded container proves nothing.
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var (after, loaded) = await framework.RunOnFrameworkThread(() =>
            {
                var item = scanner.TryReadSlot(slot, out var isLoaded);
                return (item, isLoaded);
            }).ConfigureAwait(false);
            if (!loaded)
            {
                LastFailure = "the container closed before the result could be checked";
                return false;
            }
            if (after is null || after.ItemId != expectedItemId) return true;
            await Task.Delay(2000, finish).ConfigureAwait(false);
        }
        LastFailure = "the confirmation was answered but the item is still there";
        return false;
    }

    /// <summary>
    /// Cached rows carry the cache's idea of a slot, which for retainers follows the on-screen tab layout
    /// rather than memory. Find the planned item by identity in the live container instead.
    /// </summary>
    public SlotRef? FindSlot(ContainerKind kind, ulong ownerId, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef preferred)
    {
        var candidates = OnGame(() => scanner.ScanKind(kind))
            .Where(i => i.ItemId == itemId && i.Quantity == quantity && i.IsHq == isHq && !exclude.Contains(i.Slot))
            .Where(i => kind != ContainerKind.Retainer || i.Slot.OwnerId == ownerId)
            .ToList();
        if (candidates.Count == 0) return null;
        return (candidates.FirstOrDefault(c => c.Slot == preferred) ?? candidates[0]).Slot;
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
        public static int FreeMarketSlots()
        {
            var rm = RetainerManager.Instance();
            if (rm == null) return 0;
            var r = rm->GetActiveRetainer();
            if (r == null || r->RetainerId == 0) return 0;
            return Math.Max(0, MarketSlotsPerRetainer - r->MarketItemCount);
        }

        /// <summary>Types the price and quantity into the RetainerSell window and presses its confirm callback.</summary>
        public static bool FillRetainerSell(int price, int quantity, int confirmCallback)
        {
            var unit = AddonDriver.GetAddon("RetainerSell");
            if (unit == null || !unit->IsVisible) return false;
            var addon = (AddonRetainerSell*)unit;
            if (addon->Quantity != null) addon->Quantity->SetValue(quantity);
            if (addon->AskingPrice != null) addon->AskingPrice->SetValue(price);
            var values = stackalloc AtkValue[1];
            values[0].SetInt(confirmCallback);
            return unit->FireCallback(1, values, true);
        }

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

        public enum DeliveryPick { NotListed, OtherCopyOnly, Selected }

        /// <summary>The seal reward the list showed for the piece last picked.</summary>
        public static int LastSealReward;

        /// <summary>
        /// Picks the list row for this exact piece, by the container and slot the list reports for it. The first
        /// row with the same item id could be a different copy: one with materia, or one the player kept back.
        /// </summary>
        public static DeliveryPick SelectExpertDelivery(SlotRef slot, uint itemId, int selectCallback)
        {
            var agent = AgentModule.Instance()->GetAgentGrandCompanySupply();
            var addon = AddonDriver.GetAddon("GrandCompanySupplyList");
            if (agent == null || addon == null || !addon->IsVisible || agent->ItemArray == null) return DeliveryPick.NotListed;
            var otherCopy = false;
            for (var i = 0; i < agent->NumItems; i++)
            {
                var entry = agent->ItemArray[i];
                if (entry.ItemId != itemId || !entry.IsTurnInAvailable) continue;
                if ((uint)entry.Inventory != slot.ContainerId || entry.Slot != slot.Slot) { otherCopy = true; continue; }
                var values = stackalloc AtkValue[2];
                values[0].SetInt(selectCallback);
                values[1].SetInt(entry.Position);
                LastSealReward = entry.SealReward;
                return addon->FireCallback(2, values, false) ? DeliveryPick.Selected : DeliveryPick.NotListed;
            }
            return otherCopy ? DeliveryPick.OtherCopyOnly : DeliveryPick.NotListed;
        }

        /// <summary>Which of the active retainer's market slots hold a listing.</summary>
        public static IReadOnlySet<int> OccupiedMarketSlots()
        {
            var taken = new HashSet<int>();
            var im = InventoryManager.Instance();
            var market = im == null ? null : im->GetInventoryContainer(InventoryType.RetainerMarket);
            if (market == null || !market->IsLoaded) return taken;
            for (var i = 0; i < market->Size; i++)
            {
                var item = market->GetInventorySlot(i);
                if (item != null && item->ItemId != 0) taken.Add(i);
            }
            return taken;
        }

        /// <summary>The listing that appeared since <paramref name="before"/> for this item, with its unit price.</summary>
        public static (int Slot, ulong Price)? NewListing(IReadOnlySet<int> before, uint itemId)
        {
            var im = InventoryManager.Instance();
            var market = im == null ? null : im->GetInventoryContainer(InventoryType.RetainerMarket);
            if (market == null || !market->IsLoaded) return null;
            for (var i = 0; i < market->Size; i++)
            {
                if (before.Contains(i)) continue;
                var item = market->GetInventorySlot(i);
                if (item == null || item->ItemId != itemId) continue;
                return (i, im->GetRetainerMarketPrice((short)i));
            }
            return null;
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
