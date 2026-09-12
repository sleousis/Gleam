using Dalamud.Game.Command;
using Dalamud.Interface;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Gleam.Core.Integrations;
using Gleam.Core.Logging;
using Gleam.Core.Model;
using Gleam.Core.Stats;
using Gleam.Game;
using Gleam.Integrations;
using Gleam.Services;
using Gleam.Windows;

namespace Gleam;

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

    private readonly IDalamudPluginInterface pi;
    private readonly ICommandManager commands;
    private readonly IClientState clientState;
    private readonly IPluginLog log;
    private readonly IChatGui chat;

    private readonly Configuration config;
    private readonly WindowSystem windows = new("Gleam");
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
    private readonly SelfTest selfTest;
    private readonly DebugReport report;
    private readonly StatsService stats;

    /// <summary>Said at the next login when settings or history were brought across from the old copy again.</summary>
    private string? carryOverNotice;

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

        config = LoadConfig(pi, log, chat, out carryOverNotice);
        Windows.Ui.Reduced = config.ReduceMotion;
        // Saves from background work raced the settings page over the same objects. Every save goes through
        // the game's own thread, where the page also draws.
        void Save()
        {
            if (framework.IsInFrameworkUpdateThread) config.Save(pi);
            else _ = framework.RunOnFrameworkThread(() => config.Save(pi));
        }
        if (config.Migrate()) Save();

        db = new ItemDatabase(data, log) { Curated = LoadCurated(pi, log) };
        // Headline numbers on the stats page come from a font built at their size, so they stay sharp.
        Windows.Charts.BigFont = Windows.Charts.CreateBigFont(pi.UiBuilder.FontAtlas);
        var scanner = new GameInventoryScanner(inventory, log, id => db.Get(id)?.IsEquipment == true);
        var contextBuilder = new ItemContextBuilder(player, data, db, config, log);
        dialogs = new AddonDriver(addonLifecycle, framework, log);
        var contextDriver = new InventoryContextDriver(framework, db, log);
        selfTest = new SelfTest(framework, condition, pi, config, db, contextDriver, scanner);
        var actions = new GameActions(framework, inventory, scanner, dialogs, contextDriver, db, config, log, condition);
        var mover = new MoveActions(framework, scanner, log, config, id => db.Get(id)?.StackSize ?? 1);
        var merger = new StackMerger(mover, log);
        var runLog = new JsonLinesRunLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "gleam-history.jsonl");
        allagan = new AllaganToolsSource(pi, log) { Enabled = config.UseAllaganTools, RetainerNames = GameInventoryScanner.KnownRetainers, CanHoldMateria = id => db.Get(id)?.IsEquipment == true };
        IMarketPriceSource market = new UniversalisClient();

        var snapshots = new InventorySnapshotService(framework, player, log, config, db, scanner, contextBuilder, allagan, market);
        coordinator = new RunCoordinator(framework, player, chat, toast, log, config, db, scanner, contextBuilder, actions, merger, runLog, allagan, market, snapshots, Save);
        var moveLog = new JsonLinesMoveLog(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "gleam-moves.jsonl");
        var journal = new JsonLinesJournal(new ReliableTextStorage(storage, pi.GetPluginConfigDirectory()), "gleam-journal.jsonl");
        organizer = new OrganizerCoordinator(framework, player, chat, toast, log, config, db, snapshots, mover, moveLog, coordinator);

        var icons = new IconCache(textures, Path.Combine(pi.AssemblyLocation.Directory?.FullName ?? ".", "images"));
        debugWindow = new DebugWindow(framework, actions, mover, scanner, contextDriver, db, allagan, market, player, config);
        settingsWindow = new SettingsWindow(config, player, db, icons, allagan, coordinator, () => debugWindow.IsOpen = true);
        historyWindow = new HistoryWindow(runLog, moveLog, db, icons);
        organizerPanel = new OrganizerPanel(organizer, config, db, icons, Save);
        // One window with four pages. Only troubleshooting, which almost nobody opens, stays separate.
        confirmWindow = new ConfirmationWindow(coordinator, icons, db, config, gamepad)
        {
            Organizer = organizerPanel,
            History = historyWindow,
            SettingsPage = settingsWindow,
        };
        void OpenReview() => confirmWindow.Show(confirmWindow.HomePage);
        organizerPanel.SwitchToClean = OpenReview;
        organizerPanel.OpenSettings = () => confirmWindow.Show(Ui.AppMode.Settings);
        historyWindow.Back = OpenReview;
        settingsWindow.Back = OpenReview;
        windows.AddWindow(confirmWindow);
        windows.AddWindow(debugWindow);

        coordinator.RequestOpenWindow += () => confirmWindow.Show(Ui.AppMode.Clean);   // a container opened: that is a cleaning prompt

        var nav = new VnavmeshIpc(pi);
        var travel = new LifestreamIpc(pi);
        pilot = new Automation.AutoPilot(framework, clientState, condition, objectTable, data, chat, log, config, coordinator, nav, travel, db);
        autoRetainer = new AutoRetainerIpc(pi);
        ventures = new VentureHook(autoRetainer, coordinator, config, chat, log);
        settingsWindow.AutoRetainer = autoRetainer;
        report = new DebugReport(pi, config, coordinator, organizer, pilot, runLog, moveLog, selfTest);
        selfTest.IsBusy = () => coordinator.IsRunning || organizer.IsRunning || pilot.IsRunning;
        debugWindow.SelfTest = selfTest;
        debugWindow.CopyReport = CopyReport;
        settingsWindow.CopyReport = CopyReport;

        // The stats page counts along with every run, and keeps what the histories cannot tell in a journal.
        stats = new StatsService(framework, player, chat, log, config, runLog, moveLog, journal, Save);
        coordinator.ActionFinished += stats.OnActionFinished;
        coordinator.RunFinished += stats.OnRunFinished;
        coordinator.ScanFinished += stats.OnScan;
        coordinator.DecisionsTaken += stats.OnDecisions;
        organizer.MoveFinished += stats.OnMoveFinished;
        organizer.MovesFinished += stats.OnMovesFinished;
        pilot.TripFinished += stats.OnTrip;
        actions.SealsEarned += stats.OnSeals;
        confirmWindow.Stats = new StatsPage(stats, coordinator, organizer, pilot, config, icons)
        {
            Back = OpenReview,
            OpenSettings = () => confirmWindow.Show(Ui.AppMode.Settings),
        };
        historyWindow.OpenStats = () => confirmWindow.Show(Ui.AppMode.Stats);
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
        var whatsNew = new WhatsNewCard(config);
        organizerPanel.WhatsNew = whatsNew;
        confirmWindow.WhatsNew = whatsNew;
        settingsWindow.Pilot = pilot;
        settingsWindow.Nav = nav;
        settingsWindow.Travel = travel;

        watcher = new AddonWatcher(addonLifecycle, framework);
        watcher.ContainerOpened += kind => _ = coordinator.OnContainerOpenedAsync(kind);
        watcher.ContainerOpened += kind => _ = organizer.OnContainerOpenedAsync(kind);
        watcher.ActionWindowOpened += coordinator.OnActionWindowOpened;

        contextMenu = new ContextMenuIntegration(contextMenuService, player, chat, config, db, Save)
        {
            OpenWindow = () => confirmWindow.Show(confirmWindow.HomePage),
        };

        dtr = new DtrEntry(dtrBar, toast, framework, () => confirmWindow.Show(confirmWindow.HomePage))
        {
            // No junk count for someone who has turned clearing junk off.
            CleanableCount = () => config.UseClean ? coordinator.LastCleanableCount : 0,
            OpenOrganize = () => confirmWindow.Show(Ui.AppMode.Organize),
        };
        dutyNudge = new DutyNudge(dutyState, framework, coordinator.CountCleanableAsync,
            count => toast.ShowNormal($"Gleam: {count} item{(count == 1 ? "" : "s")} could be cleaned. /gleam to review."));

        coordinator.IsOrganizing = () => organizer.IsRunning;
        config.Saved += ApplyProfileToServices;
        ApplyProfileToServices();

        this.framework = framework;
        clientState.Logout += OnLogout;
        clientState.Login += OnLogin;
        if (clientState.IsLoggedIn) { Greet(); WarnAboutOldCopy(); }

        commands.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Gleam. /gleam organize · stats · settings · history · merge · stop · selftest · report",
        });
        commands.AddHandler(ShortCommand, new CommandInfo(OnCommand) { HelpMessage = "Short for /gleam." });

        pi.UiBuilder.Draw += DrawUi;
        pi.UiBuilder.OpenMainUi += OpenMain;
        pi.UiBuilder.OpenConfigUi += OpenConfig;

        log.Information("Gleam loaded");
    }

    private void ApplyProfileToServices()
    {
        var profile = coordinator.EffectiveProfile;
        dtr.Enabled = profile.ShowDtrEntry;
        dtr.NudgePercent = profile.FullnessNudgePercent;
        // Each half's nudges and shortcuts follow whether that half is turned on.
        dutyNudge.Enabled = profile.PostDutyNudge && config.UseClean;
        dtr.OpenOrganize = config.UseOrganize ? () => confirmWindow.Show(Ui.AppMode.Organize) : null;
        allagan.Enabled = config.UseAllaganTools;
    }

    /// <summary>
    /// Settings that cannot be read are copied aside before anything else happens. Otherwise the first save
    /// writes fresh defaults over the damaged file, and every list and layout in it is gone for good.
    /// </summary>
    private static Configuration LoadConfig(IDalamudPluginInterface pi, IPluginLog log, IChatGui chat, out string? carryOverNotice)
    {
        carryOverNotice = CarryOverFromOldName(pi, log);
        Configuration? loaded = null;
        try { loaded = pi.GetPluginConfig() as Configuration; }
        catch (Exception ex) { log.Error(ex, "Gleam's settings could not be read"); }
        if (loaded is not null) return loaded;

        try
        {
            var file = pi.ConfigFile;
            if (file.Exists && file.Length > 0)
            {
                var backup = file.FullName + $".unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                file.CopyTo(backup, overwrite: false);
                chat.PrintError($"Gleam could not read its settings and started fresh. The old file was kept as {Path.GetFileName(backup)}.", "Gleam");
            }
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not keep a copy of the unreadable settings");
        }
        return new Configuration();
    }

    /// <summary>
    /// Before 0.10 Dalamud knew Gleam by its old internal name, and filed its settings and history under that.
    /// The first start under the new name brings them across: the settings with their type markers rewritten,
    /// the histories copied under their new names. The old files are left as they were, so an old copy that
    /// is still installed keeps working until it is removed.
    ///
    /// Players who kept using the old copy after that got nothing of what it saved later. Once the old copy is
    /// gone for good, anything it saved after Gleam last saved comes across once more: its settings, since the
    /// player touched those last (Gleam's are kept as a backup), and the history lines written after Gleam's
    /// newest entry. Waiting until it is uninstalled means a copy still in use never flips settings back and forth.
    /// </summary>
    /// <returns>A sentence for the player when something came across a second time; otherwise null.</returns>
    private static string? CarryOverFromOldName(IDalamudPluginInterface pi, IPluginLog log)
    {
        try
        {
            var configs = pi.ConfigFile.Directory;
            if (configs is null) return null;
            var oldSettings = new FileInfo(Path.Combine(configs.FullName, $"{LegacyNames.InternalName}.json"));
            var oldGone = !pi.InstalledPlugins.Any(p => p.InternalName == LegacyNames.InternalName);
            var said = new List<string>();

            if (!pi.ConfigFile.Exists && oldSettings.Exists && oldSettings.Length > 0)
            {
                File.WriteAllText(pi.ConfigFile.FullName, LegacyNames.RewriteConfig(File.ReadAllText(oldSettings.FullName)));
                log.Information("Brought the settings across from before the rename");
            }
            else if (oldGone && pi.ConfigFile.Exists && oldSettings.Exists && oldSettings.Length > 0
                     && oldSettings.LastWriteTimeUtc > pi.ConfigFile.LastWriteTimeUtc)
            {
                var backup = pi.ConfigFile.FullName + $".before-carry-over-{DateTime.Now:yyyyMMdd-HHmmss}";
                pi.ConfigFile.CopyTo(backup, overwrite: false);
                File.WriteAllText(pi.ConfigFile.FullName,
                    LegacyNames.MergeConfig(File.ReadAllText(oldSettings.FullName), File.ReadAllText(pi.ConfigFile.FullName)));
                log.Information("Brought across the settings the old copy saved later; the previous ones are in {Backup}", backup);
                said.Add($"the settings the older copy saved on {oldSettings.LastWriteTime:d MMM} (yours from before are kept as {Path.GetFileName(backup)})");
            }

            var oldFolder = Path.Combine(configs.FullName, LegacyNames.InternalName);
            var newFolder = pi.GetPluginConfigDirectory();
            var lines = 0;
            foreach (var (from, to) in LegacyNames.HistoryFiles)
            {
                var source = Path.Combine(oldFolder, from);
                var target = Path.Combine(newFolder, to);
                if (!File.Exists(source)) continue;
                if (!File.Exists(target)) { File.Copy(source, target); continue; }
                if (!oldGone) continue;
                var current = File.ReadAllText(target);
                var newer = LegacyNames.NewerLines(File.ReadAllText(source), current);
                if (newer.Count == 0) continue;
                File.AppendAllText(target, (current.Length > 0 && !current.EndsWith('\n') ? "\n" : string.Empty) + string.Join("\n", newer) + "\n");
                lines += newer.Count;
            }
            if (lines > 0) said.Add($"{lines} history entr{(lines == 1 ? "y" : "ies")} it recorded");

            return said.Count == 0 ? null : $"Brought across from the older copy: {string.Join(", and ", said)}.";
        }
        catch (Exception ex)
        {
            log.Warning(ex, "Could not bring the settings across from before the rename");
            return null;
        }
    }

    /// <summary>
    /// A copy installed before the rename is a different plugin to Dalamud, and both would answer /gleam and
    /// drive the same bags. Said at each login until it is gone.
    /// </summary>
    private void WarnAboutOldCopy()
    {
        if (carryOverNotice is { } notice)
        {
            chat.Print(notice, "Gleam");
            carryOverNotice = null;
        }
        if (!pi.InstalledPlugins.Any(p => p.InternalName == LegacyNames.InternalName && p.IsLoaded)) return;
        // Both copies register /gleam and the older one usually wins it, so say where the command goes until then.
        chat.PrintError("An older Gleam from before its rename is still installed, and /gleam opens that one until it is gone. Open /xlplugins and remove the Gleam at version 0.9. Anything it saves before then comes across once it is removed.", "Gleam");
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

    /// <summary>"/gleam probe yesno": what Gleam reads from the yes/no on screen, to pin down a question it misses.</summary>
    private void ProbeYesNo(bool answer)
    {
        var lines = Gleam.Game.AddonDriver.ProbeYesNo(answer, config.Callbacks.YesNoConfirm, p => db.PromptIsAbout(p, "buyback"));
        foreach (var line in lines) chat.Print(line, "Gleam");
        log.Information("Yes/no probe: {Lines}", string.Join(" | ", lines));
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "settings":
            case "config":
                confirmWindow.Show(Ui.AppMode.Settings);
                break;
            case "history":
                confirmWindow.Show(Ui.AppMode.History);
                break;
            case "organize":
            case "organise":
                if (!config.UseOrganize)
                {
                    chat.Print("Putting things away is turned off. Turn it on under Settings.", "Gleam");
                    confirmWindow.Show(Ui.AppMode.Settings);
                    break;
                }
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
            case "stats":
            case "numbers":
                confirmWindow.Show(Ui.AppMode.Stats);
                break;
            case "probe yesno":
            case "probe yesno answer":
                ProbeYesNo(answer: args.Trim().EndsWith("answer", StringComparison.OrdinalIgnoreCase));
                break;
            case "selftest":
            case "self-test":
            case "check":
                RunSelfTest();
                break;
            case "report":
                CopyReport();
                break;
            case "stop":
                var wasRunning = coordinator.IsRunning || organizer.IsRunning || (confirmWindow.Pilot?.IsRunning ?? false);
                confirmWindow.Pilot?.Stop();
                coordinator.CancelRun();
                organizer.CancelRun();
                if (!wasRunning) chat.Print("Nothing is running.", "Gleam");
                break;
            case "":
                if (confirmWindow.IsOpen) confirmWindow.IsOpen = false;
                else confirmWindow.Show(confirmWindow.HomePage);
                break;
            default:
                // An unknown word used to toggle the window, so a typo closed it. It says what exists instead.
                chat.Print("Try /gleam, /gleam organize, /gleam stats, /gleam history, /gleam settings, /gleam scan, /gleam stop, /gleam selftest or /gleam report.", "Gleam");
                break;
        }
    }

    private readonly IFramework framework;

    /// <summary>Checks what Gleam can find in the game and says how it went; the details open when something needs a look.</summary>
    private void RunSelfTest()
    {
        chat.Print("Checking what Gleam can find in the game. No item is touched.", "Gleam");
        _ = selfTest.RunAsync().ContinueWith(t =>
        {
            if (t.IsFaulted)
            {
                log.Error(t.Exception, "Self-test failed");
                chat.PrintError("The self-test could not finish. Details are in the Dalamud log.", "Gleam");
                return;
            }
            chat.Print($"Self-test: {selfTest.Summary()}", "Gleam");
            if (selfTest.Count(SelfTestResult.Fail) + selfTest.Count(SelfTestResult.Warn) > 0)
                framework.RunOnFrameworkThread(() => { if (!disposed) debugWindow.IsOpen = true; });
        });
    }

    /// <summary>Builds the bug report in the background; <see cref="DrawUi"/> puts it on the clipboard.</summary>
    private void CopyReport() => _ = report.QueueCopyAsync().ContinueWith(t =>
    {
        if (!t.IsFaulted) return;
        log.Error(t.Exception, "Bug report failed");
        chat.PrintError("Gleam could not put the report together. Details are in the Dalamud log.", "Gleam");
    });

    private void OnLogout(int type, int code)
    {
        // Nothing carries on into the next character: not a run, not its retainers, not its bag count.
        if (pilot.IsRunning) pilot.Stop();
        coordinator.OnLogout();
        organizer.OnLogout();
        Game.RetainerDirectory.ForgetLive();
        dtr.Reset();
    }
    private void OnLogin() => framework.RunOnTick(() => { if (disposed) return; Greet(); WarnAboutOldCopy(); _ = coordinator.RefreshPlanAsync(false); }, delay: TimeSpan.FromSeconds(8));

    /// <summary>Said once, ever: how to open it and that nothing happens without a click.</summary>
    private void Greet()
    {
        if (config.Greeted) return;
        config.Greeted = true;
        config.Save(pi);
        chat.Print("Gleam is ready. Type /gleam (or just /gl) to see what it thinks is junk. Nothing is discarded or sold until you press Clean.", "Gleam");
    }
    private void OpenMain() => confirmWindow.Show(confirmWindow.HomePage);
    /// <summary>
    /// Drawing happens on the game's own thread, which is the only place its retainer list may be read, so
    /// the directory is refreshed here rather than from a background task.
    /// </summary>
    private void DrawUi()
    {
        if (Game.RetainerDirectory.Poll(config)) config.Save(pi);
        // The clipboard belongs to the drawing thread; a report built in the background waits here for it.
        if (report.TakeReady() is { } text)
        {
            Dalamud.Bindings.ImGui.ImGui.SetClipboardText(text);
            chat.Print("Gleam copied a bug report to your clipboard. Paste it into an issue on GitHub. It holds no character or retainer names.", "Gleam");
        }
        windows.Draw();
    }

    private void OpenConfig() => confirmWindow.Show(Ui.AppMode.Settings);

    private bool disposed;

    public void Dispose()
    {
        disposed = true;
        pi.UiBuilder.Draw -= DrawUi;
        pi.UiBuilder.OpenMainUi -= OpenMain;
        pi.UiBuilder.OpenConfigUi -= OpenConfig;
        clientState.Logout -= OnLogout;
        clientState.Login -= OnLogin;
        config.Saved -= ApplyProfileToServices;
        commands.RemoveHandler(Command);
        commands.RemoveHandler(ShortCommand);
        windows.RemoveAllWindows();
        highlighter.Dispose();
        ventures.Dispose();
        autoRetainer.Dispose();
        pilot.Dispose();
        coordinator.Dispose();
        organizer.Dispose();
        dutyNudge.Dispose();
        dtr.Dispose();
        stats.Dispose();
        Windows.Charts.BigFont?.Dispose();
        Windows.Charts.BigFont = null;
        contextMenu.Dispose();
        watcher.Dispose();
        dialogs.Dispose();
    }
}
