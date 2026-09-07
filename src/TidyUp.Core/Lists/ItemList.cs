namespace TidyUp.Core.Lists;

/// <summary>One entry of the protect list or the always-discard list.</summary>
public sealed class ItemListEntry
{
    public uint ItemId { get; set; }

    /// <summary>When true, the HQ and NQ variants are treated as the same item (default).</summary>
    public bool IncludeHq { get; set; } = true;

    /// <summary>Null means account-wide; otherwise the entry only applies to this character.</summary>
    public ulong? CharacterId { get; set; }

    public string Note { get; set; } = string.Empty;
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;

    public bool AppliesTo(ulong characterId) => CharacterId is null || CharacterId == characterId;
}

public enum ListKind
{
    Protect,
    AlwaysDiscard,
}

/// <summary>A user-editable set of item ids. Plain class so it round-trips through plugin config.</summary>
public sealed class ItemList
{
    public List<ItemListEntry> Entries { get; set; } = new();

    public bool Contains(uint itemId, bool isHq, ulong characterId)
    {
        foreach (var e in Entries)
        {
            if (e.ItemId != itemId || !e.AppliesTo(characterId)) continue;
            if (isHq && !e.IncludeHq) continue;
            return true;
        }
        return false;
    }

    public ItemListEntry? Find(uint itemId, ulong? characterId) =>
        Entries.FirstOrDefault(e => e.ItemId == itemId && e.CharacterId == characterId);

    /// <summary>Adds or updates. Returns false when an identical entry already existed.</summary>
    public bool Add(uint itemId, ulong? characterId = null, string note = "", bool includeHq = true)
    {
        var existing = Find(itemId, characterId);
        if (existing is not null)
        {
            existing.IncludeHq = includeHq;
            if (!string.IsNullOrEmpty(note)) existing.Note = note;
            return false;
        }
        Entries.Add(new ItemListEntry { ItemId = itemId, CharacterId = characterId, Note = note, IncludeHq = includeHq });
        return true;
    }

    public int Remove(uint itemId, ulong? characterId = null) =>
        Entries.RemoveAll(e => e.ItemId == itemId && (characterId is null || e.CharacterId == characterId));

    public int RemoveAll(uint itemId) => Entries.RemoveAll(e => e.ItemId == itemId);
}
