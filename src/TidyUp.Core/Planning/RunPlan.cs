using TidyUp.Core.Lists;
using TidyUp.Core.Model;

namespace TidyUp.Core.Planning;

/// <summary>One row in the confirmation window.</summary>
public sealed class PlanRow
{
    public required Proposal Proposal { get; init; }
    public bool Checked { get; set; }

    /// <summary>Which action the user picked for this row; defaults to the proposal's.</summary>
    public ActionKind ChosenAction { get; set; }

    public ScannedItem Item => Proposal.Item;
    public ItemInfo Info => Proposal.Info;
    public bool IsExecutable => ChosenAction.IsDestructive();

    /// <summary>Stable identity for session memory: same slot + same item + same qty.</summary>
    public string Key => $"{Item.Slot}|{Item.ItemId}|{Item.Quantity}|{Item.IsHq}";
}

/// <summary>A container section: everything for one container of one owner.</summary>
public sealed class PlanSection
{
    public required ContainerKind Kind { get; init; }
    public required ulong OwnerId { get; init; }
    public required string OwnerName { get; init; }
    public List<PlanRow> Rows { get; } = new();

    /// <summary>Whether the executors can act on this container right now.</summary>
    public bool IsAvailableNow { get; set; }
    public string Requirement { get; set; } = string.Empty;

    /// <summary>Dresser only: how many free inventory slots restore needs vs. has.</summary>
    public int FreeSlotsNeeded => Kind == ContainerKind.GlamourDresser ? Rows.Count(r => r.Checked && r.IsExecutable) : 0;

    public string Title => Kind == ContainerKind.Retainer && !string.IsNullOrEmpty(OwnerName)
        ? $"Retainer: {OwnerName}"
        : Kind.DisplayName();

    public int CheckedCount => Rows.Count(r => r.Checked && r.IsExecutable);
}

/// <summary>An item the planner deliberately left out, so the window can explain itself if asked.</summary>
public sealed record ExcludedItem(ScannedItem Item, ItemInfo Info, string Reason, bool IsHardBlock);

public sealed class RunSummary
{
    public int TotalRows { get; init; }
    public int CheckedRows { get; init; }
    public Dictionary<ContainerKind, int> SlotsFreedByContainer { get; init; } = new();
    public long GilDestroyed { get; init; }
    public long GilRecovered { get; init; }
    public int SealsRows { get; init; }
    public int DesynthRows { get; init; }
    public int HardBlocked { get; init; }
    public int Protected { get; init; }
}

public sealed class RunPlan
{
    public required ulong CharacterId { get; init; }
    public required string CharacterName { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<PlanSection> Sections { get; } = new();
    public List<ExcludedItem> Excluded { get; } = new();

    /// <summary>Alt characters, read-only.</summary>
    public List<AltPreview> Alts { get; } = new();

    public IEnumerable<PlanRow> AllRows => Sections.SelectMany(s => s.Rows);

    public RunSummary Summarize()
    {
        var checkedRows = AllRows.Where(r => r.Checked && r.IsExecutable).ToList();
        var freed = new Dictionary<ContainerKind, int>();
        long destroyed = 0, recovered = 0;
        int seals = 0, desynth = 0;
        foreach (var r in checkedRows)
        {
            freed[r.Item.Slot.Kind] = freed.GetValueOrDefault(r.Item.Slot.Kind) + 1;
            switch (r.ChosenAction)
            {
                case ActionKind.Discard: destroyed += (long)r.Info.VendorPrice * r.Item.Quantity; break;
                case ActionKind.VendorSell: recovered += (long)r.Info.VendorPrice * r.Item.Quantity; break;
                case ActionKind.ExpertDelivery: seals++; break;
                case ActionKind.Desynth: desynth++; break;
            }
        }
        return new RunSummary
        {
            TotalRows = AllRows.Count(),
            CheckedRows = checkedRows.Count,
            SlotsFreedByContainer = freed,
            GilDestroyed = destroyed,
            GilRecovered = recovered,
            SealsRows = seals,
            DesynthRows = desynth,
            HardBlocked = Excluded.Count(e => e.IsHardBlock),
            Protected = Excluded.Count(e => !e.IsHardBlock),
        };
    }
}

public sealed class AltPreview
{
    public required ulong CharacterId { get; init; }
    public required string CharacterName { get; init; }
    public List<Proposal> Proposals { get; } = new();
}
