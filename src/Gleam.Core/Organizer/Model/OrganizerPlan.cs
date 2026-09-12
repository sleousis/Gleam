using Gleam.Core.Model;
using Gleam.Core.Organizer.Capacity;

namespace Gleam.Core.Organizer.Model;

public enum DestinationKind
{
    /// <summary>Leave the item wherever it is.</summary>
    Stay,
    Bags,
    /// <summary>The armoury chest; equipment only, each piece to its own slot page.</summary>
    Armoury,
    Saddlebag,
    /// <summary>A retainer: a specific one when <see cref="Destination.RetainerId"/> is set, otherwise any in scope with room.</summary>
    Retainer,
}

/// <summary>
/// Where a rule sends what it matches. A record with settable members so it round-trips through any config
/// serializer. The named destinations are fresh instances on every access on purpose: the plugin config
/// serializer fills an existing object in place, so a shared default would be overwritten by whichever
/// rule loads last and every rule would end up pointing at the same destination.
/// </summary>
public sealed record Destination
{
    public DestinationKind Kind { get; set; } = DestinationKind.Stay;
    public ulong RetainerId { get; set; }

    public static Destination Stay => new() { Kind = DestinationKind.Stay };
    public static Destination Bags => new() { Kind = DestinationKind.Bags };
    public static Destination Armoury => new() { Kind = DestinationKind.Armoury };
    public static Destination Saddlebag => new() { Kind = DestinationKind.Saddlebag };
    public static Destination AnyRetainer => new() { Kind = DestinationKind.Retainer };
    public static Destination RetainerNamed(ulong id) => new() { Kind = DestinationKind.Retainer, RetainerId = id };

    // Computed, so never written to the settings file: the loader fills objects in place, and a computed
    // value written out has already cost data once.
    [System.Runtime.Serialization.IgnoreDataMember]
    public bool IsAnyRetainer => Kind == DestinationKind.Retainer && RetainerId == 0;

    /// <summary>The storage this destination names, or null for Stay and for "any retainer" (resolved by the solver).</summary>
    [System.Runtime.Serialization.IgnoreDataMember]
    public StorageId? Storage => Kind switch
    {
        DestinationKind.Bags => new StorageId(ContainerKind.Inventory),
        DestinationKind.Armoury => new StorageId(ContainerKind.Armoury),
        DestinationKind.Saddlebag => new StorageId(ContainerKind.Saddlebag),
        DestinationKind.Retainer when RetainerId != 0 => new StorageId(ContainerKind.Retainer, RetainerId),
        _ => null,
    };
}

/// <summary>
/// What a rule matches. Every set field must hold (AND); an empty predicate matches everything, which makes
/// a sensible catch-all as the last rule.
/// </summary>
public sealed class OrganizerPredicate
{
    public HashSet<ItemTag>? Tags { get; set; }
    public HashSet<string>? UiCategories { get; set; }
    public int? MinItemLevel { get; set; }
    public int? MaxItemLevel { get; set; }
    public int? MinEquipLevel { get; set; }
    public int? MaxEquipLevel { get; set; }
    /// <summary>Gear a job the character has levelled past 1 can wear.</summary>
    public bool? ForJobsPlayed { get; set; }
    /// <summary>Gear that one of the character's gear sets uses.</summary>
    public bool? InGearset { get; set; }
    public bool? IsHq { get; set; }
    public bool? HasMateria { get; set; }
    public bool? IsStackable { get; set; }
    public bool? IsUntradable { get; set; }
    public bool? IsUnique { get; set; }
    public bool? OnNeverTouchList { get; set; }
    public HashSet<uint>? ItemIds { get; set; }

    [System.Runtime.Serialization.IgnoreDataMember]
    public bool IsEmpty =>
        Tags is null or { Count: 0 } && UiCategories is null or { Count: 0 } && MinItemLevel is null && MaxItemLevel is null
        && MinEquipLevel is null && MaxEquipLevel is null && ForJobsPlayed is null && InGearset is null && IsHq is null && HasMateria is null
        && IsStackable is null && IsUntradable is null && IsUnique is null && OnNeverTouchList is null && ItemIds is null or { Count: 0 };

    public bool Matches(ScannedItem item, ItemInfo info, ItemContext ctx, Func<uint, bool, bool> onNeverTouch)
    {
        if (Tags is { Count: > 0 } && !Tags.Contains(ItemTags.Of(info))) return false;
        if (UiCategories is { Count: > 0 } && !UiCategories.Contains(info.UiCategory)) return false;
        if (MinItemLevel is { } minIl && (!info.IsEquipment || info.ItemLevel < minIl)) return false;
        if (MaxItemLevel is { } maxIl && (!info.IsEquipment || info.ItemLevel > maxIl)) return false;
        if (MinEquipLevel is { } minLv && (!info.IsEquipment || info.LevelEquip < minLv)) return false;
        if (MaxEquipLevel is { } maxLv && (!info.IsEquipment || info.LevelEquip > maxLv)) return false;
        if (ForJobsPlayed is { } played && (!info.IsEquipment || ctx.AnyJobPlayedForCategory(info.ClassJobCategoryId) != played)) return false;
        if (InGearset is { } inSet && (!info.IsEquipment || ctx.GearsetItemIds.Contains(info.ItemId) != inSet)) return false;
        if (IsHq is { } hq && item.IsHq != hq) return false;
        if (HasMateria is { } mat && item.HasMateria != mat) return false;
        if (IsStackable is { } st && info.IsStackable != st) return false;
        if (IsUntradable is { } ut && info.IsUntradable != ut) return false;
        if (IsUnique is { } un && info.IsUnique != un) return false;
        if (OnNeverTouchList is { } prot && onNeverTouch(item.ItemId, item.IsHq) != prot) return false;
        if (ItemIds is { Count: > 0 } && !ItemIds.Contains(item.ItemId)) return false;
        return true;
    }

    /// <summary>Short human summary, e.g. "Materia · HQ" or "Gear · iL ≤ 640".</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (Tags is { Count: > 0 }) parts.Add(string.Join("/", Tags.OrderBy(t => t).Select(t => t.Label())));
        if (UiCategories is { Count: > 0 }) parts.Add(string.Join("/", UiCategories.OrderBy(c => c)));
        if (MinItemLevel is { } a && MaxItemLevel is { } b) parts.Add($"iL {a}–{b}");
        else if (MinItemLevel is { } a2) parts.Add($"iL ≥ {a2}");
        else if (MaxItemLevel is { } b2) parts.Add($"iL ≤ {b2}");
        if (MinEquipLevel is { } c && MaxEquipLevel is { } d) parts.Add($"Lv {c}–{d}");
        else if (MinEquipLevel is { } c2) parts.Add($"Lv ≥ {c2}");
        else if (MaxEquipLevel is { } d2) parts.Add($"Lv ≤ {d2}");
        if (ForJobsPlayed is { } j) parts.Add(j ? "jobs I play" : "jobs I don't play");
        if (InGearset is { } g) parts.Add(g ? "in a gear set" : "not in a gear set");
        if (IsHq is { } hq) parts.Add(hq ? "HQ" : "NQ");
        if (HasMateria is { } m) parts.Add(m ? "with materia" : "no materia");
        if (IsStackable is { } s) parts.Add(s ? "stackable" : "single");
        if (IsUntradable is { } u) parts.Add(u ? "untradeable" : "tradeable");
        if (IsUnique is { } q) parts.Add(q ? "unique" : "not unique");
        if (OnNeverTouchList is { } p) parts.Add(p ? "on Never touch" : "not on Never touch");
        if (ItemIds is { Count: > 0 }) parts.Add($"{ItemIds.Count} named item{(ItemIds.Count == 1 ? "" : "s")}");
        return parts.Count == 0 ? "everything else" : string.Join(" · ", parts);
    }
}

public sealed class OrganizerRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public OrganizerPredicate When { get; set; } = new();
    public Destination Then { get; set; } = Destination.Stay;

    /// <summary>
    /// Keep up to this many of each matched item in the bags and move only the rest; 0 moves everything.
    /// Whole stacks only, so one stack bigger than the number still stays.
    /// </summary>
    public int KeepInBags { get; set; }
}

/// <summary>A named, ordered set of rules. First matching rule wins; unmatched items follow <see cref="Fallback"/>.</summary>
public sealed class OrganizerPlan
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "My layout";

    /// <summary>
    /// True for the one layout the simple screen owns and edits. Layouts built by hand are never marked, and
    /// the simple screen never touches them.
    /// </summary>
    public bool Simple { get; set; }

    public List<OrganizerRule> Rules { get; set; } = new();
    public Destination Fallback { get; set; } = Destination.Stay;

    /// <summary>Incoming stackables merge into partial stacks at the destination (true) or take fresh slots (false).</summary>
    public bool MergeStacksAtDestination { get; set; } = true;

    /// <summary>Retainer ids the plan may use; empty means every retainer the character has.</summary>
    public HashSet<ulong> RetainersInScope { get; set; } = new();

    /// <summary>Bag slots kept free while items pass through the bags on their way between two storages.</summary>
    public int BagStagingReserve { get; set; } = 10;

    public OrganizerPlan Clone()
    {
        var c = new OrganizerPlan
        {
            Id = Guid.NewGuid(),
            Name = Name,
            Simple = false,
            Fallback = Fallback with { },
            MergeStacksAtDestination = MergeStacksAtDestination,
            RetainersInScope = new HashSet<ulong>(RetainersInScope),
            BagStagingReserve = BagStagingReserve,
        };
        foreach (var r in Rules)
        {
            c.Rules.Add(new OrganizerRule
            {
                Id = Guid.NewGuid(), Name = r.Name, Enabled = r.Enabled, Then = r.Then with { }, KeepInBags = r.KeepInBags,
                When = new OrganizerPredicate
                {
                    Tags = r.When.Tags is null ? null : new HashSet<ItemTag>(r.When.Tags),
                    UiCategories = r.When.UiCategories is null ? null : new HashSet<string>(r.When.UiCategories),
                    MinItemLevel = r.When.MinItemLevel, MaxItemLevel = r.When.MaxItemLevel,
                    MinEquipLevel = r.When.MinEquipLevel, MaxEquipLevel = r.When.MaxEquipLevel,
                    ForJobsPlayed = r.When.ForJobsPlayed, InGearset = r.When.InGearset, IsHq = r.When.IsHq, HasMateria = r.When.HasMateria,
                    IsStackable = r.When.IsStackable, IsUntradable = r.When.IsUntradable, IsUnique = r.When.IsUnique,
                    OnNeverTouchList = r.When.OnNeverTouchList,
                    ItemIds = r.When.ItemIds is null ? null : new HashSet<uint>(r.When.ItemIds),
                },
            });
        }
        return c;
    }

    /// <summary>
    /// An exact copy, ids included, for work done off the game thread while the page may edit this layout.
    /// <see cref="Clone"/> makes a new layout; this one stands in for the same layout.
    /// </summary>
    public OrganizerPlan Snapshot()
    {
        var c = Clone();
        c.Id = Id;
        c.Simple = Simple;
        for (var i = 0; i < Rules.Count; i++) c.Rules[i].Id = Rules[i].Id;
        return c;
    }

    /// <summary>A starting point most players recognise: materia and crystals to the saddlebag, consumables in the bags.</summary>
    public static OrganizerPlan Starter() => new()
    {
        Name = "Starter layout",
        Rules =
        {
            new OrganizerRule { Name = "Materia to the saddlebag", When = new OrganizerPredicate { Tags = [ItemTag.Materia] }, Then = Destination.Saddlebag },
            new OrganizerRule { Name = "Consumables in the bags", When = new OrganizerPredicate { Tags = [ItemTag.Consumables] }, Then = Destination.Bags },
            new OrganizerRule { Name = "Gear set pieces in the armoury", When = new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = true }, Then = Destination.Armoury },
            new OrganizerRule { Name = "Other gear to a retainer", When = new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = false }, Then = Destination.AnyRetainer },
            new OrganizerRule { Name = "Housing to a retainer", When = new OrganizerPredicate { Tags = [ItemTag.Housing] }, Then = Destination.AnyRetainer },
        },
    };
}
