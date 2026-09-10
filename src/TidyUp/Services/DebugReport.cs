using System.Text;
using Dalamud.Plugin;
using TidyUp.Core.Execution;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
using TidyUp.Game;

namespace TidyUp.Services;

/// <summary>
/// Everything worth knowing about a problem, as text a player pastes into a bug report: versions, language,
/// plugins, the settings that change behaviour, how the last runs went and what failed, and the last self-test.
///
/// Nothing that identifies the player goes in: no character, retainer or world names and no ids. Item names
/// do, because "the dresser refused the Ironworks gloves" is the whole point of a report.
/// </summary>
public sealed class DebugReport
{
    private readonly IDalamudPluginInterface pi;
    private readonly Configuration config;
    private readonly RunCoordinator coordinator;
    private readonly OrganizerCoordinator organizer;
    private readonly Automation.AutoPilot pilot;
    private readonly IRunLog runLog;
    private readonly IMoveLog moveLog;
    private readonly SelfTest selfTest;

    public DebugReport(IDalamudPluginInterface pi, Configuration config, RunCoordinator coordinator, OrganizerCoordinator organizer,
        Automation.AutoPilot pilot, IRunLog runLog, IMoveLog moveLog, SelfTest selfTest)
    {
        this.pi = pi;
        this.config = config;
        this.coordinator = coordinator;
        this.organizer = organizer;
        this.pilot = pilot;
        this.runLog = runLog;
        this.moveLog = moveLog;
        this.selfTest = selfTest;
    }

    private string? ready;
    private readonly object gate = new();

    /// <summary>Builds the report in the background; the next frame puts it on the clipboard.</summary>
    public async Task QueueCopyAsync()
    {
        var text = await BuildAsync().ConfigureAwait(false);
        lock (gate) ready = text;
    }

    /// <summary>Called while drawing, the only place the clipboard may be written. Null when nothing is waiting.</summary>
    public string? TakeReady()
    {
        lock (gate)
        {
            var text = ready;
            ready = null;
            return text;
        }
    }

    public async Task<string> BuildAsync()
    {
        var sb = new StringBuilder();
        var gleam = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "?";
        var dalamud = typeof(IDalamudPluginInterface).Assembly.GetName().Version?.ToString() ?? "?";
        sb.AppendLine($"Gleam {gleam} · Dalamud {dalamud} · game {GameVersionGuard.Current() ?? "unknown"} ({GameVersionGuard.Status(config)}, checked against {GameVersionGuard.CheckedAgainst})");
        sb.AppendLine($"Game language: {PluginServices.DataManager.Language}");

        bool Installed(string name) => pi.InstalledPlugins.Any(p => p.InternalName == name && p.IsLoaded);
        sb.AppendLine($"Plugins: vnavmesh {YesNo(Installed("vnavmesh"))}, Lifestream {YesNo(Installed("Lifestream"))}, AutoRetainer {YesNo(Installed("AutoRetainer"))}, Allagan Tools {YesNo(Installed("InventoryTools"))}");

        var p = coordinator.EffectiveProfile;
        var places = Enum.GetValues<ContainerKind>().Where(p.IsContainerEnabled).Select(k => k.DisplayName());
        sb.AppendLine($"Using: cleaning {OnOff(config.UseClean)}, organizing {OnOff(config.UseOrganize)}, every setting shown {OnOff(config.AdvancedMode)}, preset {p.Thresholds.Policy}");
        sb.AppendLine($"Looks in: {string.Join(", ", places)}; retainers left alone: {p.ExcludedRetainerIds.Count}");
        var s = config.Automation;
        sb.AppendLine($"Hands-free: retainers {OnOff(s.VisitRetainers)}, dresser {OnOff(s.VisitDresser)}, merchant {OnOff(s.SellAtVendor)}, Grand Company {OnOff(s.VisitGrandCompany)}, saddlebag {OnOff(s.OpenSaddlebag)}, travel {OnOff(s.TravelToInn)}, items found on the way {s.UnseenRows}, after ventures {OnOff(s.CleanAfterVentures)}, sort after {OnOff(config.SortAfterRun)}");

        sb.AppendLine();
        sb.AppendLine($"Hands-free now: {(pilot.IsRunning ? "running" : "idle")}{(string.IsNullOrEmpty(pilot.Status) ? "" : $", last step \"{pilot.Status}\"")}{(pilot.LastError is { } e ? $", last error \"{e}\"" : "")}");
        if (coordinator.LastReport is { } clean)
        {
            sb.AppendLine($"Last clean: {clean.Summary()}{(clean.Aborted ? $" (stopped: {clean.AbortReason})" : "")}");
            foreach (var r in clean.Results.Where(r => r.Outcome == ActionOutcome.Failed).Take(10))
                sb.AppendLine($"  failed: {r.Action.ItemName} ({r.Action.Action}, {r.Action.Kind}): {r.Message}");
        }
        if (organizer.LastReport is { } moves)
        {
            sb.AppendLine($"Last organize: {moves.Summary()}{(moves.Aborted ? $" (stopped: {moves.AbortReason})" : "")}");
            foreach (var r in moves.Results.Where(r => r.Status == StepStatus.Failed).Take(10))
                sb.AppendLine($"  failed: {r.Op.Info.Name} ({r.Op.From.Kind} to {r.Op.To.Kind}): {r.Message}");
        }

        try
        {
            var history = await runLog.ReadAllAsync().ConfigureAwait(false);
            var moved = await moveLog.ReadAllAsync().ConfigureAwait(false);
            sb.AppendLine($"History: {history.Count} cleaned, {moved.Count} moved");
            foreach (var h in history.Where(h => h.Outcome == ActionOutcome.Failed).TakeLast(10))
                sb.AppendLine($"  {h.At:yyyy-MM-dd HH:mm} failed: {h.ItemName} ({h.Action}, {h.Container})");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"History could not be read: {ex.Message}");
        }

        sb.AppendLine();
        if (selfTest.LastAt is { } at)
        {
            sb.AppendLine($"Self-test at {at:yyyy-MM-dd HH:mm}: {selfTest.Summary()}");
            foreach (var line in selfTest.Last.Where(l => l.Result is SelfTestResult.Fail or SelfTestResult.Warn or SelfTestResult.Note))
                sb.AppendLine($"  {line.Result}: {line.Area}: {line.Detail}");
        }
        else sb.AppendLine("Self-test: not run. /gleam selftest runs it.");
        return sb.ToString();
    }

    private static string YesNo(bool b) => b ? "yes" : "no";
    private static string OnOff(bool b) => b ? "on" : "off";
}
