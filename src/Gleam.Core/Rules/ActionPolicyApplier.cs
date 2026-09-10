using Gleam.Core.Model;

namespace Gleam.Core.Rules;

/// <summary>
/// Turns a preset's one-sentence promise into the action on each row. Runs after the rules and the
/// container constraints, before the user's per-rule overrides. Returns null when a retired sell-only
/// policy says the item should not be proposed at all.
/// </summary>
public static class ActionPolicyApplier
{
    public static Proposal? Apply(Proposal p, ActionPolicy policy)
    {
        if (policy == ActionPolicy.RuleDecides) return p;
        if (p.Action == ActionKind.None) return p;                 // show-only rows stay informational
        if (p.Confidence == Confidence.User) return p;            // the always-discard list is the user's own instruction

        var kind = p.Item.Slot.Kind;
        var canSell = !p.Info.IsUntradable && p.Info.VendorPrice > 0 && ContainerConstraints.AllowsAction(kind, ActionKind.VendorSell);
        var canList = p.Info.IsMarketable && p.MarketUnitPrice > 0 && ContainerConstraints.AllowsAction(kind, ActionKind.MarketList);

        ActionKind target;
        switch (policy)
        {
            case ActionPolicy.SellOnly:
                if (!canSell) return null;
                target = ActionKind.VendorSell;
                break;
            case ActionPolicy.MarketListTradeable:
                target = canList ? ActionKind.MarketList : canSell ? ActionKind.VendorSell : ActionKind.Discard;
                break;
            case ActionPolicy.DiscardUntradeableSellTradeable:
                target = canSell ? ActionKind.VendorSell : ActionKind.Discard;
                break;
            default:
                target = ActionKind.Discard;
                break;
        }

        if (target == p.Action) return p;

        var previous = new[] { p.Action }.Concat(p.Alternatives)
            .Where(a => a != target && a.IsDestructive() && ContainerConstraints.AllowsAction(kind, a))
            .Distinct()
            .ToList();
        var value = target switch
        {
            ActionKind.VendorSell => (long)p.Info.VendorPrice * p.Item.Quantity,
            ActionKind.MarketList => p.MarketUnitPrice * p.Item.Quantity,
            _ => 0,
        };
        // "Sellable on the market" is a nudge towards the market board; once the row *is* a listing it would only untick it.
        var warnings = target == ActionKind.MarketList
            ? p.Warnings.Where(w => w != "Sellable on the market" && !w.StartsWith("Worth about", StringComparison.Ordinal)).ToList()
            : p.Warnings;
        return p with
        {
            Action = target,
            Alternatives = previous,
            Warnings = warnings,
            ValueGil = value,
            ValueLabel = target switch
            {
                ActionKind.VendorSell => $"{value:N0}g",
                ActionKind.MarketList => $"{value:N0}g mkt",
                _ => "—",
            },
        };
    }

    public static string DropReason(ActionPolicy policy) => policy switch
    {
        ActionPolicy.SellOnly => "Sell only: untradeable or no vendor price",
        _ => string.Empty,
    };
}
