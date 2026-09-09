using Dalamud.Plugin.Services;
using TidyUp.Core.Model;
using TidyUp.Integrations;

namespace TidyUp.Services;

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

    private void OnStep(string retainer)
    {
        if (!config.Automation.CleanAfterVentures || coordinator.IsRunning || coordinator.IsPilotRunning()) return;
        ar.RequestTurn();
    }

    private void OnReady(string retainer) => _ = RunAsync(retainer);

    private async Task RunAsync(string retainer)
    {
        IsRunning = true;
        try
        {
            await coordinator.RefreshPlanAsync(openWindow: false, focus: ContainerKind.Inventory).ConfigureAwait(false);
            var queue = coordinator.BuildQueueFromPlan(r => r.Item.Slot.Kind == ContainerKind.Inventory && r.ChosenAction == ActionKind.Discard);
            if (queue.Count == 0) return;
            coordinator.SuppressChatSummary = true;
            await coordinator.ExecuteQueueAsync(queue, refreshAfter: false).ConfigureAwait(false);
            var done = coordinator.LastReport?.Done ?? 0;
            if (done > 0) chat.Print($"After {retainer}'s ventures: discarded {done} item{(done == 1 ? "" : "s")} from your bags.", "Satchel");
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Cleaning after ventures failed");
        }
        finally
        {
            coordinator.SuppressChatSummary = false;
            IsRunning = false;
            ar.FinishTurn();
        }
    }
}
