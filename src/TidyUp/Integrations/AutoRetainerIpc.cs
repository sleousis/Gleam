using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace TidyUp.Integrations;

/// <summary>
/// AutoRetainer's post-process handshake: it asks every plugin whether it wants a turn after a retainer's
/// ventures are collected, gives each requester the retainer in turn, and waits for Finish.
/// </summary>
public sealed class AutoRetainerIpc : IDisposable
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<string, object> onStep;
    private readonly ICallGateSubscriber<string, string, object> onReady;
    private readonly ICallGateSubscriber<string, object> request;
    private readonly ICallGateSubscriber<object> finish;

    /// <summary>A retainer's ventures are done; call <see cref="RequestTurn"/> right away to be given a turn.</summary>
    public event Action<string>? RetainerStep;

    /// <summary>The turn is ours; the retainer named is summoned. Call <see cref="FinishTurn"/> when done, always.</summary>
    public event Action<string>? RetainerReady;

    public AutoRetainerIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        onStep = pi.GetIpcSubscriber<string, object>("AutoRetainer.OnRetainerAdditionalTask");
        onReady = pi.GetIpcSubscriber<string, string, object>("AutoRetainer.OnRetainerReadyForPostprocess");
        request = pi.GetIpcSubscriber<string, object>("AutoRetainer.RequestPostprocess");
        finish = pi.GetIpcSubscriber<object>("AutoRetainer.FinishPostprocessRequest");
        onStep.Subscribe(OnStep);
        onReady.Subscribe(OnReady);
    }

    public bool IsInstalled => pi.InstalledPlugins.Any(p => p.InternalName == "AutoRetainer" && p.IsLoaded);

    private void OnStep(string retainer) => RetainerStep?.Invoke(retainer);

    private void OnReady(string plugin, string retainer)
    {
        if (plugin == pi.InternalName) RetainerReady?.Invoke(retainer);
    }

    public void RequestTurn()
    {
        try { request.InvokeAction(pi.InternalName); } catch { /* not installed */ }
    }

    public void FinishTurn()
    {
        try { finish.InvokeAction(); } catch { /* not installed */ }
    }

    public void Dispose()
    {
        onStep.Unsubscribe(OnStep);
        onReady.Unsubscribe(OnReady);
    }
}
