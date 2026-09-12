using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace Gleam.Integrations;

/// <summary>
/// Asks other automation plugins to stand back while Gleam works. YesAlready and TextAdvance each read a shared list
/// of names and pause while it is not empty; YesAlready empties it when it loads, so the name goes back in every frame.
/// Both used to answer Gleam's own dialogs first, so an item that was handed in was reported as failed. AutoRetainer is
/// suppressed for a hands-free trip and put back once the trip has left the bell: with its "use the bell" behaviour on,
/// it switched itself on at Gleam's bell and picked retainers alongside Gleam.
/// </summary>
public sealed class AutomationPause : IDisposable
{
    private static readonly string[] StopLists = ["YesAlready.StopRequests", "TextAdvance.StopRequests"];
    private const string Me = "Gleam";

    private readonly IDalamudPluginInterface pi;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly AutoRetainerIpc autoRetainer;
    private readonly Func<bool> working;
    private readonly Func<bool> onTrip;
    private readonly Dictionary<string, HashSet<string>> held = new();
    private readonly HashSet<string> unavailable = new();
    private bool? autoRetainerWasSuppressed;

    public AutomationPause(IDalamudPluginInterface pi, IFramework framework, IPluginLog log, AutoRetainerIpc autoRetainer,
        Func<bool> working, Func<bool> onTrip)
    {
        this.pi = pi;
        this.framework = framework;
        this.log = log;
        this.autoRetainer = autoRetainer;
        this.working = working;
        this.onTrip = onTrip;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        Release();
        RestoreAutoRetainer();
    }

    private void OnUpdate(IFramework fw)
    {
        if (working()) Hold();
        else if (held.Count > 0) Release();

        var trip = onTrip();
        if (trip && autoRetainerWasSuppressed is null && autoRetainer.Suppressed is { } was)
        {
            autoRetainerWasSuppressed = was;
            autoRetainer.SetSuppressed(true);
        }
        else if (!trip) RestoreAutoRetainer();
    }

    private void Hold()
    {
        foreach (var tag in StopLists)
        {
            if (unavailable.Contains(tag)) continue;
            try
            {
                if (!held.TryGetValue(tag, out var list)) held[tag] = list = pi.GetOrCreateData(tag, () => new HashSet<string>());
                list.Add(Me);
            }
            catch (Exception ex)
            {
                // Shared under another type by some other version: leave that plugin alone rather than retry every frame.
                unavailable.Add(tag);
                log.Debug(ex, "Could not ask {Tag} to pause", tag);
            }
        }
    }

    private void Release()
    {
        foreach (var (tag, list) in held)
        {
            try
            {
                list.Remove(Me);
                pi.RelinquishData(tag);
            }
            catch (Exception ex)
            {
                log.Debug(ex, "Could not release {Tag}", tag);
            }
        }
        held.Clear();
    }

    private void RestoreAutoRetainer()
    {
        if (autoRetainerWasSuppressed is not { } was) return;
        autoRetainer.SetSuppressed(was);
        autoRetainerWasSuppressed = null;
    }
}
