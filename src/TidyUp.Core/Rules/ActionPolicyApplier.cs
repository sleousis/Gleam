using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

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

        ActionKind target;
        switch (policy)
        {
            case ActionPolicy.SellOnly:
                if (!canSell) return null;
                target = ActionKind.VendorSell;
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
        var value = target == ActionKind.VendorSell ? (long)p.Info.VendorPrice * p.Item.Quantity : 0;
        return p with
        {
            Action = target,
            Alternatives = previous,
            ValueGil = value,
            ValueLabel = target == ActionKind.VendorSell ? $"{value:N0}g" : "—",
        };
    }

    public static string DropReason(ActionPolicy policy) => policy switch
    {
        ActionPolicy.SellOnly => "Sell-only policy: only tradeable items with a vendor price are proposed",
        _ => string.Empty,
    };
}
