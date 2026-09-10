using Gleam.Core.Model;
using Gleam.Core.Rules;

namespace Gleam.Core.Planning;

public readonly record struct SoftCapResult(bool Exceeded, int Items, long GilAtRisk, int ItemCap, long GilCap)
{
    public string ButtonLabel(string verb) => Exceeded
        ? $"{verb} {Items} (exceeds cap)"
        : $"{verb} {Items}";

    public string Explanation => Exceeded
        ? $"This run would destroy or sell {Items} stacks worth {GilAtRisk:N0}g. Your cap is {ItemCap} items or {GilCap:N0}g. Click again to proceed anyway."
        : string.Empty;
}

/// <summary>Whichever trips first: the item count or the gil value at risk.</summary>
public static class SoftCap
{
    public static SoftCapResult Evaluate(IEnumerable<PlanRow> rows, Thresholds t)
    {
        var items = 0;
        long gil = 0;
        foreach (var r in rows)
        {
            if (!r.Checked || !r.IsExecutable) continue;
            items++;
            // Gil at risk counts destroyed *and* sold value: both are irreversible property changes. A discarded
            // market item loses its market value, not its vendor price, so take whichever is higher.
            var unit = Math.Max((long)r.Info.VendorPrice, r.Proposal.MarketUnitPrice);
            gil += unit * r.Item.Quantity;
        }
        var exceeded = items > t.SoftCapItems || gil > t.SoftCapGil;
        return new SoftCapResult(exceeded, items, gil, t.SoftCapItems, t.SoftCapGil);
    }
}
