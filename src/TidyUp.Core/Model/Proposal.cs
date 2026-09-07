namespace TidyUp.Core.Model;

public enum ActionKind
{
    /// <summary>Informational row: shown, never executed (e.g. "worth more on the market").</summary>
    None,
    Discard,
    VendorSell,
    ExpertDelivery,
    Desynth,
    /// <summary>Put up for sale on the market board through a retainer, at the lowest home-world price.</summary>
    MarketList,
}

public enum Confidence
{
    Low,
    Medium,
    High,
    /// <summary>The user put this item on the always-discard list.</summary>
    User,
}

public static class ActionKindExtensions
{
    public static string Label(this ActionKind kind) => kind switch
    {
        ActionKind.None => "show only",
        ActionKind.Discard => "discard",
        ActionKind.VendorSell => "sell",
        ActionKind.ExpertDelivery => "seals",
        ActionKind.Desynth => "desynth",
        ActionKind.MarketList => "market",
        _ => kind.ToString(),
    };

    public static bool IsDestructive(this ActionKind kind) => kind != ActionKind.None;
}

/// <summary>One row of the confirmation window: what to do with one physical stack, and why.</summary>
public sealed record Proposal
{
    public required ScannedItem Item { get; init; }
    public required ItemInfo Info { get; init; }
    public required ActionKind Action { get; init; }
    public IReadOnlyList<ActionKind> Alternatives { get; init; } = Array.Empty<ActionKind>();
    public required Confidence Confidence { get; init; }
    public required string RuleId { get; init; }
    public required string Reason { get; init; }

    /// <summary>Gil recovered (sell) or destroyed (discard) by executing this row. Seals are reported in <see cref="ValueLabel"/>.</summary>
    public long ValueGil { get; init; }
    public string ValueLabel { get; init; } = "—";

    /// <summary>Lowest current home-world listing per unit for this item's quality, 0 when unknown or unmarketable.</summary>
    public long MarketUnitPrice { get; init; }

    /// <summary>For registrable items: true when already registered on this character. Null for everything else.</summary>
    public bool? Registered { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// <summary>High-confidence rows start checked; anything with a warning or low confidence starts unchecked.</summary>
    public bool DefaultChecked =>
        Action.IsDestructive() && Warnings.Count == 0 && Confidence is Confidence.High or Confidence.User;

    public bool NeedsMateriaRetrieval => Item.HasMateria && Action.IsDestructive();

    public Proposal WithAction(ActionKind action, string? reasonSuffix = null) => this with
    {
        Action = action,
        Reason = reasonSuffix is null ? Reason : $"{Reason} · {reasonSuffix}",
    };
}
