using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>Armour and weapons far below the level of every job that could wear them.</summary>
public sealed class ObsoleteGearRule : IRule
{
    public const string RuleId = "obsolete-gear";
    public string Id => RuleId;
    public string Name => "Obsolete gear";
    public string Description => "Equipment whose equip level is far below the best job that can wear it, and is in no gearset or plate.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Gear;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!info.IsEquipment || info.IsUnique || info.IsNeverProposed) return null;
        if (ctx.GearsetItemIds.Contains(info.ItemId)) return null;

        var best = ctx.MaxLevelForCategory(info.ClassJobCategoryId);
        var played = ctx.AnyJobPlayedForCategory(info.ClassJobCategoryId);
        string reason;
        if (!played)
        {
            if (!t.IncludeGearForUnplayedJobs) return null;
            reason = "Gear for a job you have never levelled";
        }
        else
        {
            if (best - info.LevelEquip < t.ObsoleteGearLevelGap) return null;
            reason = $"Obsolete gear · lv{info.LevelEquip} vs best job lv{best}";
        }

        var action = GearActions.Rank(item, info, ctx, out var alternatives, out var valueGil, out var valueLabel);
        var warnings = new List<string>();
        if (item.HasMateria) warnings.Add($"Retrieve materia first ({item.MateriaCount} slotted)");
        if (info.IsMarketable) warnings.Add("Sellable on the market");

        return new Proposal
        {
            Item = item, Info = info,
            Action = action,
            Alternatives = alternatives,
            Confidence = played ? Confidence.High : Confidence.Medium,
            RuleId = Id,
            Reason = reason,
            ValueGil = valueGil,
            ValueLabel = valueLabel,
            Warnings = warnings,
        };
    }
}

/// <summary>Gear bought with tomestones or scrips that can no longer be earned.</summary>
public sealed class RetiredCurrencyGearRule : IRule
{
    public const string RuleId = "retired-currency-gear";
    public string Id => RuleId;
    public string Name => "Obsolete tomestone and scrip gear";
    public string Description => "Equipment sold for a retired tomestone or scrip, below your current tier, in no gearset or plate.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Gear;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!info.IsEquipment || !ctx.RetiredCurrencyGearIds.Contains(info.ItemId)) return null;
        if (ctx.GearsetItemIds.Contains(info.ItemId)) return null;
        if (ctx.MaxGearsetItemLevel > 0 && info.ItemLevel >= ctx.MaxGearsetItemLevel) return null;

        var action = GearActions.Rank(item, info, ctx, out var alternatives, out var valueGil, out var valueLabel);
        var warnings = new List<string>();
        if (item.HasMateria) warnings.Add($"Retrieve materia first ({item.MateriaCount} slotted)");

        return new Proposal
        {
            Item = item, Info = info,
            Action = action,
            Alternatives = alternatives,
            Confidence = Confidence.High,
            RuleId = Id,
            Reason = $"Bought with a retired currency · iL{info.ItemLevel}",
            ValueGil = valueGil,
            ValueLabel = valueLabel,
            Warnings = warnings,
        };
    }
}

/// <summary>Picks the best action for a piece of gear and lists the others as alternatives.</summary>
public static class GearActions
{
    /// <summary>Expert Delivery wants uncommon-or-better, tradeable-or-not gear; the officer refuses grey items.</summary>
    public static bool IsExpertDeliveryCandidate(ItemInfo info) => info.IsEquipment && info.Rarity >= 2 && !info.IsUnique;

    public static ActionKind Rank(ScannedItem item, ItemInfo info, ItemContext ctx,
        out IReadOnlyList<ActionKind> alternatives, out long valueGil, out string valueLabel)
    {
        var ranked = new List<ActionKind>();
        if (IsExpertDeliveryCandidate(info)) ranked.Add(ActionKind.ExpertDelivery);
        if (info.IsDesynthable) ranked.Add(ActionKind.Desynth);
        if (info.VendorPrice > 0) ranked.Add(ActionKind.VendorSell);
        ranked.Add(ActionKind.Discard);

        var primary = ranked[0];
        alternatives = ranked.Skip(1).ToList();
        valueGil = primary == ActionKind.VendorSell ? (long)info.VendorPrice * item.Quantity : 0;
        valueLabel = primary switch
        {
            ActionKind.ExpertDelivery => "seals",
            ActionKind.Desynth => "materials",
            ActionKind.VendorSell => Gil.Label(valueGil),
            _ => "—",
        };
        return primary;
    }
}
