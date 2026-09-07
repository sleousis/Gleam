using TidyUp.Core.Model;

namespace TidyUp.Core.Rules;

/// <summary>A rule looks at one stack and either proposes something or stays quiet.</summary>
public interface IRule
{
    string Id { get; }
    string Name { get; }
    string Description { get; }

    /// <summary>Which containers this rule applies to. Rules outside their containers are never even asked.</summary>
    IReadOnlySet<ContainerKind> Containers { get; }

    Proposal? Evaluate(ScannedItem item, ItemInfo info, ItemContext ctx, Thresholds t);
}

/// <summary>Runs after all rules, over the full proposal list (market pricing, action ranking).</summary>
public interface IProposalPostProcessor
{
    string Id { get; }
    IReadOnlyList<Proposal> Process(IReadOnlyList<Proposal> proposals, ItemContext ctx, Thresholds t);
}

internal static class RuleContainers
{
    public static readonly IReadOnlySet<ContainerKind> Storage = new HashSet<ContainerKind>
    {
        ContainerKind.Inventory, ContainerKind.Saddlebag, ContainerKind.Retainer,
    };

    public static readonly IReadOnlySet<ContainerKind> Gear = new HashSet<ContainerKind>
    {
        ContainerKind.Inventory, ContainerKind.Armoury, ContainerKind.Saddlebag, ContainerKind.Retainer,
    };

    public static readonly IReadOnlySet<ContainerKind> DresserOnly = new HashSet<ContainerKind>
    {
        ContainerKind.GlamourDresser,
    };

    public static readonly IReadOnlySet<ContainerKind> All = new HashSet<ContainerKind>(Enum.GetValues<ContainerKind>());
}

internal static class Gil
{
    public static string Label(long gil) => gil <= 0 ? "—" : $"{gil:N0}g";
}
