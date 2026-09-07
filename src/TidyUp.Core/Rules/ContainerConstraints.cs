using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>
/// What the game physically allows per container. Selling, Expert Delivery and desynthesis only work
/// on items in the player's own bags; something stored with a retainer or in the saddlebag can only be
/// discarded where it sits. Applied last, unconditionally, so no rule or override can produce an
/// action the executor could never complete.
/// </summary>
public static class ContainerConstraints
{
    public static bool AllowsAction(ContainerKind kind, ActionKind action) => action switch
    {
        ActionKind.None or ActionKind.Discard => true,
        // A retainer lists what is in the player's bags or in its own inventory; armoury pieces must be moved first.
        ActionKind.MarketList => kind is ContainerKind.Inventory or ContainerKind.Retainer,
        _ => kind is ContainerKind.Inventory or ContainerKind.Armoury,
    };

    public static Proposal Apply(Proposal p)
    {
        var kind = p.Item.Slot.Kind;
        if (AllowsAction(kind, p.Action))
        {
            var alts = p.Alternatives.Where(a => AllowsAction(kind, a)).ToList();
            return alts.Count == p.Alternatives.Count ? p : p with { Alternatives = alts };
        }

        var where = kind switch
        {
            ContainerKind.Retainer => "stored with a retainer",
            ContainerKind.Saddlebag => "in the saddlebag",
            ContainerKind.GlamourDresser => "in the dresser",
            _ => "not in your bags",
        };
        return p with
        {
            Action = ActionKind.Discard,
            Alternatives = Array.Empty<ActionKind>(),
            Reason = $"{p.Reason} · {where}: discard only, withdraw it to {p.Action.Label()}",
            ValueGil = 0,
            ValueLabel = "—",
        };
    }

    public static IReadOnlyList<Proposal> Apply(IReadOnlyList<Proposal> proposals) => proposals.Select(Apply).ToList();
}
