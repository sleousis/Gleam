using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>
/// The dresser's killer rule: items that no glamour plate references. Restoring one of these breaks nothing.
/// Refuses to run unless plate data was actually loaded this session, because an empty plate set would
/// otherwise mark every dresser item as unreferenced.
/// </summary>
public sealed class DresserZeroPlatesRule : IRule
{
    public const string RuleId = "dresser-zero-plates";
    public string Id => RuleId;
    public string Name => "Dresser items in zero plates";
    public string Description => "Glamour dresser items that no glamour plate uses. Restoring then discarding them frees dresser slots without touching any plate.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.DresserOnly;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (item.Slot.Kind != ContainerKind.GlamourDresser) return null;
        if (!ctx.PlatesLoaded) return null;
        if (ctx.PlateItemIds.Contains(info.ItemId)) return null;
        if (info.IsUnique && info.IsUntradable) return null; // hard block territory, but be explicit

        var warnings = new List<string> { "Restored to inventory first, then discarded" };
        if (item.IsDyed) warnings.Add("Dye is lost");
        if (ctx.GearsetItemIds.Contains(info.ItemId)) warnings.Add("Same item id is in a gearset");

        return new Proposal
        {
            Item = item, Info = info,
            Action = ActionKind.Discard,
            Alternatives = info.VendorPrice > 0 ? [ActionKind.VendorSell] : [],
            Confidence = Confidence.High,
            RuleId = Id,
            Reason = "In 0 plates",
            ValueGil = 0,
            // The restore step is expected, not a reason to uncheck: only real warnings should.
            Warnings = warnings.Count > 1 ? warnings.Skip(1).ToList() : [],
        };
    }
}
