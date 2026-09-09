using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TidyUp.Core.Integrations;
using TidyUp.Core.Logging;
using TidyUp.Game;
using TidyUp.Integrations;
using TidyUp.Services;
using TidyUp.Windows;

namespace TidyUp;

/// <summary>Static access to the few services windows need without threading them through every constructor.</summary>
internal static class PluginServices
{
    internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    internal static IDataManager DataManager { get; private set; } = null!;

    internal static void Init(IDalamudPluginInterface pi, IDataManager data)
    {
        PluginInterface = pi;
        DataManager = data;
    }
}

public sealed class Plugin : IDalamudPlugin
{
    private const string Command = "/tidyup";

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly IChatGui chat;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("TidyUp");
    private readonly ConfirmationWindow confirmWindow;
    private readonly SettingsWindow settingsWindow;
    private readonly HistoryWindow historyWindow;
    private readonly DebugWindow debugWindow;
    private readonly OrganizerCoordinator organizer;
    private readonly OrganizerWindow organizerWindow;

    private readonly ItemDatabase db;
    private readonly AddonDriver dialogs;
    private readonly AddonWatcher watcher;
    private readonly ContextMenuIntegration contextMenu;
    private readonly DtrEntry dtr;
    private readonly DutyNudge dutyNudge;
    private readonly RunCoordinator coordinator;
    private readonly AllaganToolsSource allagan;
    private readonly Automation.AutoPilot pilot;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager commands, IClientState clientState, IPluginLog log,
        IFramework framework, IDataManager data, IPlayerState player, IGameInventory inventory, IAddonLifecycle addonLifecycle,
        IContextMenu contextMenuService, IChatGui chat, IToastGui toast, IDtrBar dtrBar, IDutyState dutyState,
        ITextureProvider textures, IReliableFileStorage storage, IGamepadState gamepad, ICondition condition, IObjectTable objectTable)
    {
        this.pi = pi;
        this.commands = commands;
        this.clientState = clientState;
        this.log = log;
        this.chat = chat;
        PluginServices.Init(pi, data);

        config = pi.GetPluginConfig() as Configuration ?? new Configuration();
        void Save() => config.Save(pi);
        if (config.Migrate()) Save();

        db = new ItemDatabase(data, log) { Curated = LoadCurated(pi, log) };
        var scanner = new GameInventoryScanner(inventory, log, id => db.Get(id)?.IsEquipment == true);
        var contextBuilder = new ItemContextBuilder(player, data, db, config, log);
        dialogs = new AddonDriver(addonLifecycle, framework, log);
        var contextDriver = new InventoryContextDriver(framework, db, log);
        var actions = new GameActions(framework, inventory, scanner, dialogs, contextDriver, db, config, log, condition);
        var mover = new MoveActions(framework, scanner, log, config);
        var merger = new StackMerger(mover, log);
        var runLog = new JsonLinesRunLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "tidyup-history.jsonl");
        allagan = new AllaganToolsSource(pi, log) { Enabled = config.UseAllaganTools, RetainerNames = GameInventoryScanner.KnownRetainers, CanHoldMateria = id => db.Get(id)?.IsEquipment == true };
        IMarketPriceSource market = new UniversalisClient();

        var snapshots = new InventorySnapshotService(framework, player, log, config, db, scanner, contextBuilder, allagan, market);
        coordinator = new RunCoordinator(framework, player, chat, toast, log, config, db, scanner, contextBuilder, actions, merger, runLog, allagan, market, snapshots, Save);
        var moveLog = new JsonLinesMoveLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "tidyup-moves.jsonl");
        organizer = new OrganizerCoordinator(framework, player, chat, toast, log, config, db, snapshots, mover, moveLog, coordinator);

        var icons = new IconCache(textures, Path.Combine(pi.AssemblyLocation.Directory?.FullName ?? ".", "images", "icon.png"));
        debugWindow = new DebugWindow(framework, actions, mover, scanner, contextDriver, db, allagan, market, player, config);
        settingsWindow = new SettingsWindow(config, player, db, icons, allagan, coordinator, () => debugWindow.IsOpen = true);
        historyWindow = new HistoryWindow(runLog, db, icons);
        organizerWindow = new OrganizerWindow(organizer, config, db, icons, Save);
        confirmWindow = new ConfirmationWindow(coordinator, icons, db, config, gamepad, () => settingsWindow.IsOpen = true, () => historyWindow.IsOpen = true,
            () => { organizerWindow.IsOpen = true; _ = organizer.PreviewAsync(); });
        void OpenReview() { confirmWindow.IsOpen = true; if (coordinator.CurrentPlan is null) _ = coordinator.RefreshPlanAsync(openWindow: false); }
        void OpenOrganizer() { organizerWindow.IsOpen = true; if (organizer.Current is null) _ = organizer.PreviewAsync(); }
        organizerWindow.AddNav(FontAwesomeIcon.Broom, "Clean", OpenReview);
        organizerWindow.AddNav(FontAwesomeIcon.Cog, "Settings", () => settingsWindow.IsOpen = true);
        historyWindow.AddNav(FontAwesomeIcon.Broom, "Clean", OpenReview);
        historyWindow.AddNav(FontAwesomeIcon.Cog, "Settings", () => settingsWindow.IsOpen = true);
        settingsWindow.AddNav(FontAwesomeIcon.Broom, "Clean", OpenReview);
        settingsWindow.AddNav(FontAwesomeIcon.BoxOpen, "Organize", OpenOrganizer);
        settingsWindow.AddNav(FontAwesomeIcon.History, "History", () => historyWindow.IsOpen = true);
        windows.AddWindow(confirmWindow);
        windows.AddWindow(organizerWindow);
        windows.AddWindow(settingsWindow);
        windows.AddWindow(historyWindow);
        windows.AddWindow(debugWindow);

        coordinator.RequestOpenWindow += () => confirmWindow.IsOpen = true;

        var nav = new VnavmeshIpc(pi);
        var travel = new LifestreamIpc(pi);
        pilot = new Automation.AutoPilot(framework, clientState, condition, objectTable, data, chat, log, config, coordinator, nav, travel, db);
        coordinator.IsPilotRunning = () => pilot.IsRunning || organizer.IsRunning;
        organizer.IsPilotRunning = () => pilot.IsRunning;
        pilot.Organizer = organizer;
        organizerWindow.Pilot = pilot;
        confirmWindow.Pilot = pilot;
        settingsWindow.Pilot = pilot;
        settingsWindow.Nav = nav;
        settingsWindow.Travel = travel;

        watcher = new AddonWatcher(addonLifecycle, framework);
        watcher.ContainerOpened += kind => _ = coordinator.OnContainerOpenedAsync(kind);
        watcher.ContainerOpened += kind => _ = organizer.OnContainerOpenedAsync(kind);
        watcher.ActionWindowOpened += coordinator.OnActionWindowOpened;

        contextMenu = new ContextMenuIntegration(contextMenuService, player, chat, config, db, Save);

        dtr = new DtrEntry(dtrBar, toast, framework, () => { confirmWindow.IsOpen = true; _ = coordinator.RefreshPlanAsync(false); })
        {
            CleanableCount = () => coordinator.LastCleanableCount,
        };
        dutyNudge = new DutyNudge(dutyState, framework, coordinator.CountCleanableAsync,
            count => toast.ShowNormal($"Tidy Up: {count} item{(count == 1 ? "" : "s")} could be cleaned. /tidyup to review."));

        config.Saved += ApplyProfileToServices;
        ApplyProfileToServices();

        this.framework = framework;
        clientState.Logout += OnLogout;
        clientState.Login += OnLogin;

        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Tidy Up. /tidyup organize · settings · history · scan · merge · stop",
        });

        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += OpenMain;
        pi.UiBuilder.OpenConfigUi += OpenConfig;

        log.Information("Tidy Up loaded");
    }

    private void ApplyProfileToServices()
    {
        var profile = coordinator.EffectiveProfile;
        dtr.Enabled = profile.ShowDtrEntry;
        dtr.NudgePercent = profile.FullnessNudgePercent;
        dutyNudge.Enabled = profile.PostDutyNudge;
        allagan.Enabled = config.UseAllaganTools;
    }

    private static CuratedData LoadCurated(IDalamudPluginInterface pi, IPluginLog log)
    {
        try
        {
            var path = Path.Combine(pi.AssemblyLocation.Directory?.FullName ?? ".", "Data", "curated.json");
            if (!File.Exists(path)) path = Path.Combine(pi.AssemblyLocation.Directory?.FullName ?? ".", "curated.json");
            return File.Exists(path) ? CuratedData.Parse(File.ReadAllText(path)) : CuratedData.Empty;
        }
        catch (Exception ex)
        {
            log.Warning(ex, "curated.json unreadable; seasonal and scrip lists are empty");
            return CuratedData.Empty;
        }
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "settings":
            case "config":
                settingsWindow.Toggle();
                break;
            case "history":
                historyWindow.Toggle();
                break;
            case "organize":
            case "organise":
                if (organizerWindow.IsOpen) organizerWindow.IsOpen = false;
                else { organizerWindow.IsOpen = true; _ = organizer.PreviewAsync(); }
                break;
            case "spikes":
            case "troubleshoot":
            case "debug":
                debugWindow.Toggle();
                break;
            case "scan":
                _ = coordinator.RefreshPlanAsync(openWindow: true);
                break;
            case "merge":
                _ = coordinator.StackMergeAsync().ContinueWith(t =>
                {
                    if (t.IsFaulted) { log.Error(t.Exception, "Merge failed"); return; }
                    chat.Print(t.Result > 0 ? $"Merged {t.Result} split stack{(t.Result == 1 ? "" : "s")}." : "Nothing to merge.", "Tidy Up");
                });
                break;
            case "stop":
                var wasRunning = coordinator.IsRunning || organizer.IsRunning || (confirmWindow.Pilot?.IsRunning ?? false);
                confirmWindow.Pilot?.Stop();
                coordinator.CancelRun();
                organizer.CancelRun();
                if (!wasRunning) chat.Print("Nothing is running.", "Tidy Up");
                break;
            default:
                if (confirmWindow.IsOpen) confirmWindow.IsOpen = false;
                else { confirmWindow.IsOpen = true; _ = coordinator.RefreshPlanAsync(openWindow: false); }
                break;
        }
    }

    private readonly IFramework framework;

    private void OnLogout(int type, int code) { coordinator.OnLogout(); organizer.OnLogout(); }
    private void OnLogin() => framework.RunOnTick(() => _ = coordinator.RefreshPlanAsync(false), delay: TimeSpan.FromSeconds(8));
    private void OpenMain() { confirmWindow.IsOpen = true; _ = coordinator.RefreshPlanAsync(false); }
    private void OpenConfig() => settingsWindow.IsOpen = true;

    public void Dispose()
    {
        pi.UiBuilder.Draw -= windows.Draw;
        pi.UiBuilder.OpenMainUi -= OpenMain;
        pi.UiBuilder.OpenConfigUi -= OpenConfig;
        clientState.Logout -= OnLogout;
        clientState.Login -= OnLogin;
        config.Saved -= ApplyProfileToServices;
        commands.RemoveHandler(Command);
        windows.RemoveAllWindows();
        pilot.Dispose();
        coordinator.Dispose();
        organizer.Dispose();
        dutyNudge.Dispose();
        dtr.Dispose();
        contextMenu.Dispose();
        watcher.Dispose();
        dialogs.Dispose();
    }
}
