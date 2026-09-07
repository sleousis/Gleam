using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>
/// Vendor vs market. Never *adds* proposals; it re-routes the ones the rules made:
/// if the market clearly beats the vendor, the row becomes show-only ("list it yourself");
/// if the row was a discard but the vendor pays, it becomes a vendor sell.
/// </summary>
public sealed class MarketPricePostProcessor : IProposalPostProcessor
{
    public const string RuleId = "vendor-vs-market";
    public string Id => RuleId;

    public IReadOnlyList<Proposal> Process(IReadOnlyList<Proposal> proposals, ItemContext ctx, Thresholds t)
    {
        var result = new List<Proposal>(proposals.Count);
        foreach (var p in proposals)
        {
            result.Add(Apply(p, ctx, t));
        }
        return result;
    }

    public static Proposal Apply(Proposal p, ItemContext ctx, Thresholds t)
    {
        if (p.Confidence == Confidence.User) return p; // the user said always-discard; respect it
        if (!p.Action.IsDestructive()) return p;

        var qty = p.Item.Quantity;
        var vendorTotal = (long)p.Info.VendorPrice * qty;

        if (p.Info.IsMarketable && ctx.MarketPrices.TryGetValue(p.Info.ItemId, out var market))
        {
            var unit = market.MinFor(p.Item.IsHq);
            if (unit > 0)
            {
                var marketTotal = (long)Math.Floor(unit * qty * (1 - t.MarketTaxRate));
                var beatsVendor = marketTotal > vendorTotal * t.MarketPremiumFactor;
                if (beatsVendor && marketTotal >= t.MarketMinStackValueGil)
                {
                    return p with
                    {
                        Action = ActionKind.None,
                        Alternatives = [p.Action, .. p.Alternatives],
                        Reason = $"{p.Reason} · worth {marketTotal:N0}g on the market, list it yourself",
                        ValueGil = marketTotal,
                        ValueLabel = $"{marketTotal:N0}g mkt",
                        Warnings = [.. p.Warnings, "Market value exceeds vendor value"],
                    };
                }
            }
        }

        // A marketable item whose price could not be fetched is not "worthless"; say so and start unchecked.
        if (ctx.MarketLookupAttempted && p.Info.IsMarketable && !ctx.MarketPrices.ContainsKey(p.Info.ItemId)
            && !p.Warnings.Contains("Market price unavailable"))
        {
            p = p with { Warnings = [.. p.Warnings, "Market price unavailable"] };
        }

        if (p.Action == ActionKind.Discard && vendorTotal > 0)
        {
            return p with
            {
                Action = ActionKind.VendorSell,
                Alternatives = [ActionKind.Discard, .. p.Alternatives.Where(a => a != ActionKind.Discard)],
                Reason = $"{p.Reason} · vendor pays {vendorTotal:N0}g",
                ValueGil = vendorTotal,
                ValueLabel = Gil.Label(vendorTotal),
            };
        }

        return p;
    }
}
