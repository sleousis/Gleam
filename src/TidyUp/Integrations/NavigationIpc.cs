using System.Numerics;
using Dalamud.Plugin;
using Dalamud.Plugin.Ipc;

namespace TidyUp.Integrations;

/// <summary>vnavmesh over IPC: pathfind and walk to a point. Degrades to unavailable when not installed.</summary>
public sealed class VnavmeshIpc
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<bool> isReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> moveCloseTo;
    private readonly ICallGateSubscriber<bool> pathfindInProgress;
    private readonly ICallGateSubscriber<bool> pathIsRunning;
    private readonly ICallGateSubscriber<object> stop;

    public VnavmeshIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        isReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        moveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        pathfindInProgress = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        pathIsRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        stop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
    }

    public bool IsInstalled => pi.InstalledPlugins.Any(p => p.InternalName == "vnavmesh" && p.IsLoaded);

    public bool IsReady
    {
        get { try { return IsInstalled && isReady.InvokeFunc(); } catch { return false; } }
    }

    public bool MoveCloseTo(Vector3 destination, float range)
    {
        try { return moveCloseTo.InvokeFunc(destination, false, range); } catch { return false; }
    }

    public bool IsMoving
    {
        get { try { return pathfindInProgress.InvokeFunc() || pathIsRunning.InvokeFunc(); } catch { return false; } }
    }

    public void Stop()
    {
        try { stop.InvokeAction(); } catch { /* not installed */ }
    }
}

/// <summary>Lifestream over IPC: the inn shortcut and its busy flag.</summary>
public sealed class LifestreamIpc
{
    private readonly IDalamudPluginInterface pi;
    private readonly ICallGateSubscriber<bool> isBusy;
    private readonly ICallGateSubscriber<object> abort;
    private readonly ICallGateSubscriber<int?, object> enqueueInn;
    private readonly ICallGateSubscriber<string, object> executeCommand;

    public LifestreamIpc(IDalamudPluginInterface pi)
    {
        this.pi = pi;
        isBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        abort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        enqueueInn = pi.GetIpcSubscriber<int?, object>("Lifestream.EnqueueInnShortcut");
        executeCommand = pi.GetIpcSubscriber<string, object>("Lifestream.ExecuteCommand");
    }

    public bool IsInstalled => pi.InstalledPlugins.Any(p => p.InternalName == "Lifestream" && p.IsLoaded);

    public bool IsBusy
    {
        get { try { return isBusy.InvokeFunc(); } catch { return false; } }
    }

    /// <summary>Teleports to an inn and walks into the room. Falls back to the text command if the typed IPC is missing.</summary>
    public bool GoToInn(int? innIndex)
    {
        try { enqueueInn.InvokeAction(innIndex); return true; }
        catch
        {
            try { executeCommand.InvokeAction("inn"); return true; } catch { return false; }
        }
    }

    public void Abort()
    {
        try { abort.InvokeAction(); } catch { /* not installed */ }
    }
}
