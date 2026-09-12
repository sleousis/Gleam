using Gleam.Core.Model;

namespace Gleam.Core.Rules;

/// <summary>
/// Vendor vs market. Never *adds* proposals; it annotates and re-routes the ones the rules made:
/// if the market clearly beats the vendor, the row keeps its action but carries a warning with the
/// market value and starts unchecked; if the row was a discard but the vendor pays, it becomes a sell.
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
                    // Still actionable: the preset decides what happens. The warning keeps it unchecked until you look.
                    p = p with
                    {
                        ValueLabel = $"{marketTotal:N0}g mkt",
                        Warnings = [.. p.Warnings, $"Worth about {marketTotal:N0}g on the market board"],
                    };
                }
            }
        }

        // A marketable item whose price could not be fetched is not "worthless"; say so and start unchecked.
        // No entry, or an entry with no listings for this quality. Universalis answers 0 when nothing is for sale,
        // and 0 used to read as "worth nothing": a rare spare with no listings on the home world was sold for its
        // vendor price, or discarded.
        if (ctx.MarketLookupAttempted && p.Info.IsMarketable
            && (!ctx.MarketPrices.TryGetValue(p.Info.ItemId, out var known) || known.MinFor(p.Item.IsHq) <= 0)
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
