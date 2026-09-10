using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;

namespace Gleam.Core.Organizer.Execution;

public enum MoveStatus
{
    Done,
    /// <summary>The game did not accept the move (destination full, item not allowed there, container closed).</summary>
    Refused,
    /// <summary>The move was sent but the slots never reflected it.</summary>
    NotConfirmed,
    /// <summary>The source slot no longer held the expected stack; nothing was moved.</summary>
    SourceChanged,
}

public readonly record struct MoveOutcome(MoveStatus Status, string Message)
{
    public static MoveOutcome Ok => new(MoveStatus.Done, "moved");
}

/// <summary>
/// The game surface the organizer needs: read slots, find where a stack should land, and move it with
/// confirmation. The plugin implements it over the game's inventory manager; tests use an in-memory fake.
/// </summary>
public interface IMoveActions
{
    /// <summary>Whether the storage can be read and written right now (bags always; saddlebag and retainers when open).</summary>
    bool IsOpen(StorageId storage);

    ScannedItem? ReadSlot(SlotRef slot);

    /// <summary>Finds the live slot holding exactly this stack in the storage, skipping slots already acted on.</summary>
    SlotRef? FindSlot(StorageId storage, uint itemId, int quantity, bool isHq, IReadOnlySet<SlotRef> exclude, SlotRef? preferred);

    /// <summary>
    /// Where an incoming stack of <paramref name="quantity"/> should go: a partial stack of the same item and
    /// quality with room for all of it first (the game merges), else the first empty slot, preferring
    /// <paramref name="preferredPage"/> when it is non-zero. Null when nothing there can take it.
    /// Slots in <paramref name="reserved"/> are already promised to earlier moves this run. With
    /// <paramref name="emptyOnly"/> it never merges: a relay's first leg needs a slot of its own.
    /// </summary>
    SlotRef? FindLanding(StorageId storage, uint itemId, bool isHq, int quantity, uint preferredPage, IReadOnlySet<SlotRef> reserved, bool emptyOnly = false);

    /// <summary>Slot counts of the pages the game currently has loaded.</summary>
    IReadOnlyDictionary<(StorageId Storage, uint Page), int> LiveSizes();

    /// <summary>Moves the whole stack from one slot to another and waits until both slots show it happened.</summary>
    Task<MoveOutcome> MoveAsync(SlotRef from, SlotRef to, uint itemId, int quantity, CancellationToken ct);
}
