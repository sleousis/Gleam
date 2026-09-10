using TidyUp.Core.Model;

namespace TidyUp.Core.Organizer.Execution;

/// <summary>
/// Where each relay's first leg left its stack in the bags. A move from one retainer to another passes
/// through the bags in two legs, often in two different runs, and the second leg must move exactly the
/// stack the first leg brought in. Finding "a stack like it" instead once shipped the player's own
/// identical bag stack to the retainer. Owned by whoever runs moves, so it outlives a single run.
/// </summary>
public sealed class RelayLedger
{
    private readonly Dictionary<Guid, SlotRef> landed = new();

    public void Record(Guid moveId, SlotRef slot) => landed[moveId] = slot;
    public bool TryGet(Guid moveId, out SlotRef slot) => landed.TryGetValue(moveId, out slot);
    public void Forget(Guid moveId) => landed.Remove(moveId);
    public void Clear() => landed.Clear();
    public int Count => landed.Count;
}
