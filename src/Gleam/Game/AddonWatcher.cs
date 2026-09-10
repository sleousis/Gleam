using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using Gleam.Core.Model;

namespace Gleam.Game;

/// <summary>Turns "a container window just opened / closed" into events the coordinator can resume on.</summary>
public sealed class AddonWatcher : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;

    private static readonly Dictionary<string, ContainerKind> ContainerAddons = new()
    {
        ["InventoryBuddy"] = ContainerKind.Saddlebag,
        ["InventoryRetainer"] = ContainerKind.Retainer,
        ["InventoryRetainerLarge"] = ContainerKind.Retainer,
        ["MiragePrismPrismBox"] = ContainerKind.GlamourDresser,
    };

    private static readonly string[] ActionAddons = ["Shop", "GrandCompanySupplyList", "RetainerSellList"];

    public event Action<ContainerKind>? ContainerOpened;
    public event Action<ContainerKind>? ContainerClosed;
    public event Action<string>? ActionWindowOpened;

    public AddonWatcher(IAddonLifecycle lifecycle, IFramework framework)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        lifecycle.RegisterListener(AddonEvent.PostSetup, ContainerAddons.Keys, OnContainerSetup);
        lifecycle.RegisterListener(AddonEvent.PreFinalize, ContainerAddons.Keys, OnContainerFinalize);
        lifecycle.RegisterListener(AddonEvent.PostSetup, ActionAddons, OnActionSetup);
    }

    public void Dispose()
    {
        lifecycle.UnregisterListener(AddonEvent.PostSetup, ContainerAddons.Keys, OnContainerSetup);
        lifecycle.UnregisterListener(AddonEvent.PreFinalize, ContainerAddons.Keys, OnContainerFinalize);
        lifecycle.UnregisterListener(AddonEvent.PostSetup, ActionAddons, OnActionSetup);
    }

    private void OnContainerSetup(AddonEvent type, AddonArgs args)
    {
        if (!ContainerAddons.TryGetValue(args.AddonName, out var kind)) return;
        // Data (retainer pages, prism box) finishes loading a few frames after the window appears.
        framework.RunOnTick(() => ContainerOpened?.Invoke(kind), delayTicks: 20);
    }

    private void OnContainerFinalize(AddonEvent type, AddonArgs args)
    {
        if (ContainerAddons.TryGetValue(args.AddonName, out var kind)) ContainerClosed?.Invoke(kind);
    }

    private void OnActionSetup(AddonEvent type, AddonArgs args) =>
        framework.RunOnTick(() => ActionWindowOpened?.Invoke(args.AddonName), delayTicks: 5);
}
