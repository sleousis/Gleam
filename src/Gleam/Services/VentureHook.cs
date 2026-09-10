using Dalamud.Plugin.Services;
using Gleam.Core.Model;
using Gleam.Integrations;

namespace Gleam.Services;

/// <summary>
/// After AutoRetainer collects a retainer's ventures, discard the junk that landed in the bags: the rows
/// the rules would tick by default, discards only, nothing that needs a merchant or a window.
/// </summary>
public sealed class VentureHook : IDisposable
{
    private readonly AutoRetainerIpc ar;
    private readonly RunCoordinator coordinator;
    private readonly Configuration config;
    private readonly IChatGui chat;
    private readonly IPluginLog log;

    public bool IsRunning { get; private set; }

    public VentureHook(AutoRetainerIpc ar, RunCoordinator coordinator, Configuration config, IChatGui chat, IPluginLog log)
    {
        this.ar = ar;
        this.coordinator = coordinator;
        this.config = config;
        this.chat = chat;
        this.log = log;
        ar.RetainerStep += OnStep;
        ar.RetainerReady += OnReady;
    }

    public void Dispose()
    {
        ar.RetainerStep -= OnStep;
        ar.RetainerReady -= OnReady;
    }

    private bool saidHeldByPatch;

    private void OnStep(string retainer)
    {
        if (!config.UseClean || !config.Automation.CleanAfterVentures || coordinator.IsRunning || coordinator.IsPilotRunning()) return;
        // This runs while the player is away, so a patch nobody has checked Gleam against holds it back.
        if (!Game.GameVersionGuard.AllowsUnattended(config))
        {
            if (!saidHeldByPatch) chat.Print($"Not cleaning after ventures: {Game.GameVersionGuard.HeldReason}. Settings has the choice to go ahead anyway.", "Gleam");
            saidHeldByPatch = true;
            return;
        }
        ar.RequestTurn();
    }

    private void OnReady(string retainer) => _ = RunAsync(retainer);

    private async Task RunAsync(string retainer)
    {
        IsRunning = true;
        try
        {
            // A fresh look or nothing at all. The plan already on screen may hold the player's own hand-ticks,
            // and those are never carried out while they are away.
            if (!await coordinator.RefreshPlanAsync(openWindow: false, focus: ContainerKind.Inventory).ConfigureAwait(false)) return;
            var queue = coordinator.BuildQueueFromPlan(r => r.Item.Slot.Kind == ContainerKind.Inventory && r.ChosenAction == ActionKind.Discard
                && r.IsSuggested && r.Proposal.DefaultChecked && !coordinator.SessionSkips.Contains(r.Key), requireChecked: false);
            if (queue.Count == 0) return;
            coordinator.SuppressChatSummary = true;
            var before = coordinator.LastReport;
            await coordinator.ExecuteQueueAsync(queue, refreshAfter: false).ConfigureAwait(false);
            // A run that did not start leaves the previous report in place; its count is not this run's.
            var done = ReferenceEquals(coordinator.LastReport, before) ? 0 : coordinator.LastReport?.Done ?? 0;
            if (done > 0) chat.Print($"After {retainer}'s ventures: discarded {done} item{(done == 1 ? "" : "s")} from your bags.", "Gleam");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Cleaning after ventures failed");
            chat.PrintError("Cleaning after a retainer's ventures did not finish. Details are in the Dalamud log.", "Gleam");
        }
        finally
        {
            coordinator.SuppressChatSummary = false;
            IsRunning = false;
            ar.FinishTurn();
            // The window goes back to the whole list, not the bags-only look this took.
            _ = coordinator.RefreshPlanAsync(openWindow: false);
        }
    }
}
