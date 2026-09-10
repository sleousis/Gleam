using Gleam.Core.Model;

namespace Gleam.Core.Execution;

/// <summary>
/// The only surface the execution engine uses to touch the game. The plugin implements it with
/// ClientStructs; tests implement it with an in-memory fake.
/// Every method runs on the framework thread and completes when the game has confirmed the change.
/// </summary>
public interface IGameActions
{
    bool IsContainerAvailable(ContainerKind kind, ulong ownerId);

    /// <summary>Why the most recent action returned false, if the implementation knows.</summary>
    string? LastFailure { get; }

    /// <summary>Live re-read of a slot. Null when empty or unreadable.</summary>
    ScannedItem? ReadSlot(SlotRef slot);

    /// <summary>
    /// Finds a live slot holding exactly this item, quantity and quality in the given container, skipping
    /// slots already acted on this run. Planned slots can come from a cache whose numbering differs from
    /// the game's, so identity is what gets trusted, never position.
    /// </summary>
    SlotRef? FindSlot(ContainerKind kind, ulong ownerId, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef preferred);

    int FreeInventorySlots();

    /// <summary>Whether the NPC window an action needs is open (vendor shop, GC officer, desynth is always available).</summary>
    bool IsActionAvailable(ActionKind action);
    string ActionRequirement(ActionKind action);

    Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct);

    /// <summary>Restores a dresser item to the inventory. Returns the inventory slot it landed in, or null.</summary>
    Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct);

    /// <summary>Whether the game offers materia retrieval on items sitting in this container (bags and armoury only).</summary>
    bool CanRetrieveMateriaIn(ContainerKind kind);

    /// <summary>Brings an item from a retainer back into the player's bags. Returns where it landed, or null.</summary>
    Task<SlotRef?> MoveToInventoryAsync(SlotRef slot, uint itemId, int quantity, bool isHq, CancellationToken ct);

    Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct);
    Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct);

    /// <summary>Puts the stack up for sale through the open retainer at the given unit price.</summary>
    Task<bool> MarketListAsync(SlotRef slot, uint itemId, long unitPrice, int quantity, CancellationToken ct);

    /// <summary>How many of the active retainer's 20 market slots are free; 0 when no retainer is open.</summary>
    int FreeMarketSlots();
    Task<bool> ExpertDeliveryAsync(SlotRef slot, uint itemId, CancellationToken ct);
    Task<bool> DesynthAsync(SlotRef slot, uint itemId, CancellationToken ct);
}

/// <summary>Injectable delay so tests do not sleep.</summary>
public interface IDelay
{
    Task Wait(TimeSpan duration, CancellationToken ct);
}

public sealed class RealDelay : IDelay
{
    public Task Wait(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, ct);
}

public sealed class NoDelay : IDelay
{
    public Task Wait(TimeSpan duration, CancellationToken ct) => Task.CompletedTask;
}
