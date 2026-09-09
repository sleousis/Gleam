using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using TidyUp.Core.Integrations;
using TidyUp.Core.Logging;
using TidyUp.Core.Model;
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
    private const string Command = "/gleam";
    private const string ShortCommand = "/gl";
    private const string LegacyCommand = "/tidyup";

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
    private readonly OrganizerPanel organizerPanel;

    private readonly ItemDatabase db;
    private readonly AddonDriver dialogs;
    private readonly AddonWatcher watcher;
    private readonly ContextMenuIntegration contextMenu;
    private readonly DtrEntry dtr;
    private readonly DutyNudge dutyNudge;
    private readonly RunCoordinator coordinator;
    private readonly AllaganToolsSource allagan;
    private readonly Automation.AutoPilot pilot;
    private readonly AutoRetainerIpc autoRetainer;
    private readonly VentureHook ventures;
    private readonly BagHighlighter highlighter;

    public Plugin(
        IDalamudPluginInterface pi, ICommandManager commands, IClientState clientState, IPluginLog log,
        IFramework framework, IDataManager data, IPlayerState player, IGameInventory inventory, IAddonLifecycle addonLifecycle,
        IContextMenu contextMenuService, IChatGui chat, IToastGui toast, IDtrBar dtrBar, IDutyState dutyState,
        ITextureProvider textures, IReliableFileStorage storage, IGamepadState gamepad, ICondition condition, IObjectTable objectTable, IGameGui gameGui)
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
        var mover = new MoveActions(framework, scanner, log, config, id => db.Get(id)?.StackSize ?? 1);
        var merger = new StackMerger(mover, log);
        var runLog = new JsonLinesRunLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "tidyup-history.jsonl");
        allagan = new AllaganToolsSource(pi, log) { Enabled = config.UseAllaganTools, RetainerNames = GameInventoryScanner.KnownRetainers, CanHoldMateria = id => db.Get(id)?.IsEquipment == true };
        IMarketPriceSource market = new UniversalisClient();

        var snapshots = new InventorySnapshotService(framework, player, log, config, db, scanner, contextBuilder, allagan, market);
        coordinator = new RunCoordinator(framework, player, chat, toast, log, config, db, scanner, contextBuilder, actions, merger, runLog, allagan, market, snapshots, Save);
        var moveLog = new JsonLinesMoveLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "tidyup-moves.jsonl");
        organizer = new OrganizerCoordinator(framework, player, chat, toast, log, config, db, snapshots, mover, moveLog, coordinator);

        var icons = new IconCache(textures, Path.Combine(pi.AssemblyLocation.Directory?.FullName ?? ".", "images"));
        debugWindow = new DebugWindow(framework, actions, mover, scanner, contextDriver, db, allagan, market, player, config);
        settingsWindow = new SettingsWindow(config, player, db, icons, allagan, coordinator, () => debugWindow.IsOpen = true);
        historyWindow = new HistoryWindow(runLog, db, icons);
        organizerPanel = new OrganizerPanel(organizer, config, db, icons, Save);
        confirmWindow = new ConfirmationWindow(coordinator, icons, db, config, gamepad, () => settingsWindow.IsOpen = true, () => historyWindow.IsOpen = true)
        {
            Organizer = organizerPanel,
        };
        organizerPanel.SwitchToClean = () => confirmWindow.Show(Ui.AppMode.Clean);
        void OpenReview() => confirmWindow.Show(Ui.AppMode.Clean);
        void OpenOrganizer() => confirmWindow.Show(Ui.AppMode.Organize);
        historyWindow.AddNav(FontAwesomeIcon.Broom, "Clean", OpenReview);
        historyWindow.AddNav(FontAwesomeIcon.Cog, "Settings", () => settingsWindow.IsOpen = true);
        settingsWindow.AddNav(FontAwesomeIcon.Broom, "Clean", OpenReview);
        settingsWindow.AddNav(FontAwesomeIcon.BoxOpen, "Organize", OpenOrganizer);
        settingsWindow.AddNav(FontAwesomeIcon.History, "History", () => historyWindow.IsOpen = true);
        windows.AddWindow(confirmWindow);
        windows.AddWindow(settingsWindow);
        windows.AddWindow(historyWindow);
        windows.AddWindow(debugWindow);

        coordinator.RequestOpenWindow += () => confirmWindow.Show(Ui.AppMode.Clean);

        var nav = new VnavmeshIpc(pi);
        var travel = new LifestreamIpc(pi);
        pilot = new Automation.AutoPilot(framework, clientState, condition, objectTable, data, chat, log, config, coordinator, nav, travel, db);
        autoRetainer = new AutoRetainerIpc(pi);
        ventures = new VentureHook(autoRetainer, coordinator, config, chat, log);
        settingsWindow.AutoRetainer = autoRetainer;
        coordinator.IsPilotRunning = () => pilot.IsRunning || organizer.IsRunning || ventures.IsRunning;

        // Tint the game's own bag windows: gold for what the review will clean, blue for what the organizer will move.
        highlighter = new BagHighlighter(framework, gameGui, log)
        {
            Source = () =>
            {
                var tints = new Dictionary<SlotRef, System.Numerics.Vector4>();
                if (confirmWindow.IsOpen && confirmWindow.Mode == Ui.AppMode.Organize && organizer.Current is { } solve)
                    foreach (var m in solve.Moves) tints[m.Item.Slot] = BagHighlighter.MoveTint;
                if (confirmWindow.IsOpen && confirmWindow.Mode == Ui.AppMode.Clean && coordinator.CurrentPlan is { } plan)
                    foreach (var row in plan.AllRows)
                        if (row.Checked && row.IsExecutable) tints[row.Item.Slot] = BagHighlighter.CleanTint;
                return tints;
            },
        };
        organizer.IsPilotRunning = () => pilot.IsRunning;
        pilot.Organizer = organizer;
        organizerPanel.Pilot = pilot;
        confirmWindow.Pilot = pilot;
        settingsWindow.Pilot = pilot;
        settingsWindow.Nav = nav;
        settingsWindow.Travel = travel;

        watcher = new AddonWatcher(addonLifecycle, framework);
        watcher.ContainerOpened += kind => _ = coordinator.OnContainerOpenedAsync(kind);
        watcher.ContainerOpened += kind => _ = organizer.OnContainerOpenedAsync(kind);
        watcher.ActionWindowOpened += coordinator.OnActionWindowOpened;

        contextMenu = new ContextMenuIntegration(contextMenuService, player, chat, config, db, Save);

        dtr = new DtrEntry(dtrBar, toast, framework, () => confirmWindow.Show(Ui.AppMode.Clean))
        {
            CleanableCount = () => coordinator.LastCleanableCount,
        };
        dutyNudge = new DutyNudge(dutyState, framework, coordinator.CountCleanableAsync,
            count => toast.ShowNormal($"Gleam: {count} item{(count == 1 ? "" : "s")} could be cleaned. /gleam to review."));

        config.Saved += ApplyProfileToServices;
        ApplyProfileToServices();

        this.framework = framework;
        clientState.Logout += OnLogout;
        clientState.Login += OnLogin;
        if (clientState.IsLoggedIn) Greet();

        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Gleam. /gleam organize · settings · history · merge · stop",
        });
        commands.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Short for /gleam." });
        commands.AddHandler(LegacyCommand, new CommandInfo(OnCommand) { ShowInHelp = false });

        pi.UiBuilder.Draw += windows.Draw;
        pi.UiBuilder.OpenMainUi += OpenMain;
        pi.UiBuilder.OpenConfigUi += OpenConfig;

        log.Information("Gleam loaded");
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
                if (confirmWindow.IsOpen && confirmWindow.Mode == Ui.AppMode.Organize) confirmWindow.IsOpen = false;
                else confirmWindow.Show(Ui.AppMode.Organize);
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
                    chat.Print(t.Result > 0 ? $"Merged {t.Result} split stack{(t.Result == 1 ? "" : "s")}." : "Nothing to merge.", "Gleam");
                });
                break;
            case "stop":
                var wasRunning = coordinator.IsRunning || organizer.IsRunning || (confirmWindow.Pilot?.IsRunning ?? false);
                confirmWindow.Pilot?.Stop();
                coordinator.CancelRun();
                organizer.CancelRun();
                if (!wasRunning) chat.Print("Nothing is running.", "Gleam");
                break;
            default:
                if (confirmWindow.IsOpen) confirmWindow.IsOpen = false;
                else confirmWindow.Show(Ui.AppMode.Clean);
                break;
        }
    }

    private readonly IFramework framework;

    private void OnLogout(int type, int code) { coordinator.OnLogout(); organizer.OnLogout(); }
    private void OnLogin() => framework.RunOnTick(() => { Greet(); _ = coordinator.RefreshPlanAsync(false); }, delay: TimeSpan.FromSeconds(8));

    /// <summary>Said once, ever: how to open it and that nothing happens without a click.</summary>
    private void Greet()
    {
        if (config.Greeted) return;
        config.Greeted = true;
        config.Save(pi);
        chat.Print("Gleam is ready. Type /gleam (or just /gl) to see what it thinks is junk. Nothing is discarded or sold until you press Clean.", "Gleam");
    }
    private void OpenMain() => confirmWindow.Show(Ui.AppMode.Clean);
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
        commands.RemoveHandler(ShortCommand);
        commands.RemoveHandler(LegacyCommand);
        windows.RemoveAllWindows();
        highlighter.Dispose();
        ventures.Dispose();
        autoRetainer.Dispose();
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
