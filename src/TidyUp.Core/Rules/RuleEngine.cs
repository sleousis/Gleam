using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>Owns the rule catalog and turns scanned items into proposals.</summary>
public sealed class RuleEngine
{
    public static IReadOnlyList<IRule> AllRules { get; } =
    [
        new VendorOnlyJunkRule(),
        new ObsoleteGearRule(),
        new DresserZeroPlatesRule(),
        new OutleveledConsumablesRule(),
        new UnusableCraftingMatsRule(),
        new SeasonalItemsRule(),
        new RetiredCurrencyGearRule(),
    ];

    public static IReadOnlyList<string> AllRuleIds { get; } =
        [.. AllRules.Select(r => r.Id), MarketPricePostProcessor.RuleId];

    private readonly IReadOnlyList<IRule> rules;
    private readonly IReadOnlyList<IProposalPostProcessor> postProcessors;

    public RuleEngine(IReadOnlyList<IRule>? rules = null, IReadOnlyList<IProposalPostProcessor>? postProcessors = null)
    {
        this.rules = rules ?? AllRules;
        this.postProcessors = postProcessors ?? [new MarketPricePostProcessor()];
    }

    /// <summary>
    /// Evaluates every enabled rule against every item. When several rules fire for one stack the
    /// most confident wins and the others' actions become alternatives.
    /// </summary>
    public IReadOnlyList<Proposal> Evaluate(
        IEnumerable<ScannedItem> items,
        Func<uint, ItemInfo?> infoLookup,
        ItemContext ctx,
        Thresholds thresholds,
        IReadOnlySet<string>? enabledRuleIds = null)
    {
        var proposals = new List<Proposal>();
        foreach (var item in items)
        {
            var info = infoLookup(item.ItemId);
            if (info is null) continue;

            Proposal? best = null;
            var extraAlternatives = new List<ActionKind>();
            foreach (var rule in rules)
            {
                if (enabledRuleIds is not null && !enabledRuleIds.Contains(rule.Id)) continue;
                if (!rule.Containers.Contains(item.Slot.Kind)) continue;

                var p = rule.Evaluate(item, info, ctx, thresholds);
                if (p is null) continue;
                if (best is null || p.Confidence > best.Confidence)
                {
                    if (best is not null) extraAlternatives.Add(best.Action);
                    best = p;
                }
                else
                {
                    extraAlternatives.Add(p.Action);
                }
            }

            if (best is null) continue;
            if (extraAlternatives.Count > 0)
            {
                var alts = best.Alternatives.Concat(extraAlternatives)
                    .Where(a => a != best.Action && a.IsDestructive()).Distinct().ToList();
                best = best with { Alternatives = alts };
            }
            proposals.Add(best);
        }

        IReadOnlyList<Proposal> result = proposals;
        foreach (var post in postProcessors)
        {
            if (enabledRuleIds is not null && !enabledRuleIds.Contains(post.Id)) continue;
            result = post.Process(result, ctx, thresholds);
        }
        return result;
    }
}
