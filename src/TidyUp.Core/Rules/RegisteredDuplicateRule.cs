using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>
/// A minion, mount, orchestrion roll, card, emote or similar that this character has already registered
/// is a spare copy. It can go: sell it if a vendor pays, otherwise discard it.
/// </summary>
public sealed class RegisteredDuplicateRule : IRule
{
    public const string RuleId = "registered-duplicate";
    public string Id => RuleId;
    public string Name => "Already registered";
    public string Description => "Minions, mounts, orchestrion rolls, cards and the like that you have already registered.";
    public IReadOnlySet<ContainerKind> Containers => RuleContainers.Storage;

    public Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t)
    {
        if (!ctx.Registered.TryGetValue(info.ItemId, out var registered) || !registered) return null;
        var vendor = (long)info.VendorPrice * item.Quantity;
        var canSell = !info.IsUntradable && info.VendorPrice > 0;
        return new Proposal
        {
            Item = item, Info = info,
            Action = canSell ? ActionKind.VendorSell : ActionKind.Discard,
            Alternatives = canSell ? [ActionKind.Discard] : [],
            Confidence = Confidence.High,
            RuleId = Id,
            Reason = "Already registered on this character",
            ValueGil = canSell ? vendor : 0,
            ValueLabel = Gil.Label(canSell ? vendor : 0),
        };
    }
}
