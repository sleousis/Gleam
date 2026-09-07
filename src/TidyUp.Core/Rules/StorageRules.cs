using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>Untradeable or vendor-only things whose only remaining use is an NPC vendor, or nothing at all.</summary>
public sealed class VendorOnlyJunkRule : IRule
{
    public const string RuleId = "vendor-only-junk";
    public string Id => RuleId;
    public string Name => "Vendor-only junk";
    public string Description => "Items that cannot be sold on the market and are only worth vendoring, or have no use at all.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Storage;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (info.IsMarketable || info.IsEquipment || info.IsUnique || info.IsNeverProposed) return null;
        if (info.IsConsumable || info.IsMaterial) return null; // those have their own rules
        if (item.IsCollectable) return null;

        if (info.VendorPrice > 0)
        {
            if (info.VendorPrice > t.VendorOnlyMaxUnitPrice) return null;
            var value = (long)info.VendorPrice * item.Quantity;
            return new Proposal
            {
                Item = item, Info = info,
                Action = ActionKind.VendorSell,
                Alternatives = [ActionKind.Discard],
                Confidence = Confidence.High,
                RuleId = Id,
                Reason = $"Vendor-only junk · {info.VendorPrice:N0}g each",
                ValueGil = value,
                ValueLabel = Gil.Label(value),
            };
        }

        // Worth nothing anywhere and not used by any recipe: dead weight.
        if (info.IsUntradable && ctx.RecipesUsing(info.ItemId).Count == 0)
        {
            return new Proposal
            {
                Item = item, Info = info,
                Action = ActionKind.Discard,
                Confidence = Confidence.Medium,
                RuleId = Id,
                Reason = "Untradeable, unsellable, used in no recipe",
                ValueGil = 0,
            };
        }

        return null;
    }
}

/// <summary>Food, potions, and ethers far below what the character actually wears.</summary>
public sealed class OutleveledConsumablesRule : IRule
{
    public const string RuleId = "outleveled-consumables";
    public string Id => RuleId;
    public string Name => "Outleveled consumables";
    public string Description => "Meals and medicine whose item level is far below your best gearset.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Storage;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!info.IsConsumable || ctx.MaxGearsetItemLevel <= 0) return null;
        var gap = ctx.MaxGearsetItemLevel - info.ItemLevel;
        if (gap < t.ConsumableItemLevelGap) return null;

        var value = (long)info.VendorPrice * item.Quantity;
        var action = info.VendorPrice > 0 ? ActionKind.VendorSell : ActionKind.Discard;
        return new Proposal
        {
            Item = item, Info = info,
            Action = action,
            Alternatives = action == ActionKind.VendorSell ? [ActionKind.Discard] : [],
            Confidence = info.IsMarketable ? Confidence.Medium : Confidence.High,
            RuleId = Id,
            Reason = $"Outleveled consumable · iL{info.ItemLevel} vs your iL{ctx.MaxGearsetItemLevel}",
            ValueGil = value,
            ValueLabel = Gil.Label(value),
            Warnings = info.IsMarketable ? ["Sellable on the market"] : [],
        };
    }
}

/// <summary>Low-level materials every one of your crafters has long outgrown, and which cannot be sold on the market.</summary>
public sealed class UnusableCraftingMatsRule : IRule
{
    public const string RuleId = "unusable-crafting-mats";
    public string Id => RuleId;
    public string Name => "Unusable crafting mats";
    public string Description => "Vendor-only materials used only in low-level recipes your crafters have far surpassed.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Storage;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!info.IsMaterial || info.IsMarketable || info.IsNeverProposed || item.IsCollectable) return null;
        var uses = ctx.RecipesUsing(info.ItemId);
        if (uses.Count == 0) return null; // VendorOnlyJunkRule covers "no recipe at all"

        foreach (var use in uses)
        {
            if (use.RequiredLevel > t.CraftingMatMaxRecipeLevel) return null;
            if (ctx.LevelOf(use.CraftJobId) < use.RequiredLevel + t.CraftingMatCrafterLeadLevels) return null;
        }

        var maxRecipe = uses.Max(u => u.RequiredLevel);
        var value = (long)info.VendorPrice * item.Quantity;
        return new Proposal
        {
            Item = item, Info = info,
            Action = info.VendorPrice > 0 ? ActionKind.VendorSell : ActionKind.Discard,
            Alternatives = info.VendorPrice > 0 ? [ActionKind.Discard] : [],
            Confidence = Confidence.High,
            RuleId = Id,
            Reason = $"Unusable crafting mat · recipes ≤ lv{maxRecipe}, crafters far past it",
            ValueGil = value,
            ValueLabel = Gil.Label(value),
        };
    }
}

/// <summary>Items on the curated past-seasonal-event list. Always low confidence: the list is hand maintained.</summary>
public sealed class SeasonalItemsRule : IRule
{
    public const string RuleId = "past-seasonal-items";
    public string Id => RuleId;
    public string Name => "Past seasonal items";
    public string Description => "Event-locked consumables and tokens from seasonal events that already ended (curated list).";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Storage;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!ctx.SeasonalItemIds.Contains(info.ItemId) || info.IsNeverProposed) return null;
        return new Proposal
        {
            Item = item, Info = info,
            Action = ActionKind.Discard,
            Confidence = Confidence.Low,
            RuleId = Id,
            Reason = "Past seasonal event item",
            ValueGil = 0,
            Warnings = ["From a curated list; review before accepting"],
        };
    }
}
