using TidyUp.Core.Model;

namespace TidyUp.Core.Execution;

/// <summary>
/// The only surface the execution engine uses to touch the game. The plugin implements it with
/// ClientStructs; tests implement it with an in-memory fake.
/// Every method runs on the framework thread and completes when the game has confirmed the change.
/// </summary>
public interface IGameActions
{
    bool IsContainerAvailable(ContainerKind kind, ulong ownerId);

    /// <summary>Live re-read of a slot. Null when empty or unreadable.</summary>
    ScannedItem? ReadSlot(SlotRef slot);

    int FreeInventorySlots();

    /// <summary>Whether the NPC window an action needs is open (vendor shop, GC officer, desynth is always available).</summary>
    bool IsActionAvailable(ActionKind action);
    string ActionRequirement(ActionKind action);

    Task<bool> DiscardAsync(SlotRef slot, uint itemId, CancellationToken ct);

    /// <summary>Restores a dresser item to the inventory. Returns the inventory slot it landed in, or null.</summary>
    Task<SlotRef?> RestoreFromDresserAsync(SlotRef dresserSlot, uint itemId, CancellationToken ct);

    Task<bool> RetrieveMateriaAsync(SlotRef slot, uint itemId, CancellationToken ct);
    Task<bool> VendorSellAsync(SlotRef slot, uint itemId, CancellationToken ct);
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
