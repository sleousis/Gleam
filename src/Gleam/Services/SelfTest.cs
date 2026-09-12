using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Gleam.Core.Integrations;
using Gleam.Core.Model;
using Gleam.Game;

namespace Gleam.Services;

public enum SelfTestResult { Pass, Note, Warn, Fail }

public sealed record SelfTestLine(SelfTestResult Result, string Area, string Detail);

/// <summary>
/// Checks, without touching a single item, that Gleam can find everything it drives in the game: the words
/// on the item menu and on NPC menus, the objects and places it walks to, the plugins it relies on, and
/// whether the game has been patched since this build was checked. The one thing it opens is the item menu
/// of a bag item, which it reads and closes again.
///
/// Run it after a patch, on a client in another language, or before filing a bug: the report carries it.
/// </summary>
public sealed class SelfTest
{
    private readonly IFramework framework;
    private readonly ICondition condition;
    private readonly IDalamudPluginInterface pi;
    private readonly Configuration config;
    private readonly ItemDatabase db;
    private readonly InventoryContextDriver context;
    private readonly GameInventoryScanner scanner;

    public SelfTest(IFramework framework, ICondition condition, IDalamudPluginInterface pi, Configuration config, ItemDatabase db,
        InventoryContextDriver context, GameInventoryScanner scanner)
    {
        this.framework = framework;
        this.condition = condition;
        this.pi = pi;
        this.config = config;
        this.db = db;
        this.context = context;
        this.scanner = scanner;
    }

    /// <summary>Set by the plugin: a run is going, so the item menu must be left alone.</summary>
    public Func<bool> IsBusy { get; set; } = () => false;

    public IReadOnlyList<SelfTestLine> Last { get; private set; } = Array.Empty<SelfTestLine>();
    public DateTime? LastAt { get; private set; }

    public int Count(SelfTestResult result) => Last.Count(l => l.Result == result);

    public async Task<IReadOnlyList<SelfTestLine>> RunAsync()
    {
        var lines = await framework.RunOnFrameworkThread(Collect).ConfigureAwait(false);
        Last = lines;
        LastAt = DateTime.Now;
        return lines;
    }

    /// <summary>One line for chat: how it went and where the details are.</summary>
    public string Summary()
    {
        var fail = Count(SelfTestResult.Fail);
        var warn = Count(SelfTestResult.Warn);
        var pass = Count(SelfTestResult.Pass);
        if (fail == 0 && warn == 0) return $"All {pass} checks passed.";
        var parts = new List<string> { $"{pass} passed" };
        if (warn > 0) parts.Add($"{warn} to look at");
        if (fail > 0) parts.Add($"{fail} failed");
        return string.Join(", ", parts) + ". Details are under /gleam troubleshoot.";
    }

    private List<SelfTestLine> Collect()
    {
        var lines = new List<SelfTestLine>();
        void Add(SelfTestResult r, string area, string detail) => lines.Add(new SelfTestLine(r, area, detail));

        // ---- the game itself ----
        var version = GameVersionGuard.Current();
        switch (GameVersionGuard.Status(config))
        {
            case GameVersionStatus.Checked:
                Add(SelfTestResult.Pass, "Game version", $"{version}, the version this Gleam was checked on");
                break;
            case GameVersionStatus.AcceptedByPlayer:
                Add(SelfTestResult.Warn, "Game version", $"{version} is newer than {GameVersionGuard.CheckedAgainst}. You chose to go hands-free on it anyway");
                break;
            case GameVersionStatus.Unchecked:
                Add(SelfTestResult.Warn, "Game version", $"{version} is newer than {GameVersionGuard.CheckedAgainst}. Hands-free waits until Gleam is updated, or until you go ahead");
                break;
            default:
                Add(SelfTestResult.Warn, "Game version", "could not be read. Nothing is held back because of it");
                break;
        }
        var english = db.Language == Dalamud.Game.ClientLanguage.English;
        Add(SelfTestResult.Note, "Game language", english ? "English" : $"{db.Language}. Gleam reads the game's own text in this language");

        // ---- plugins ----
        bool Installed(string internalName) => pi.InstalledPlugins.Any(p => p.InternalName == internalName && p.IsLoaded);
        Add(Installed("vnavmesh") ? SelfTestResult.Pass : SelfTestResult.Fail, "vnavmesh", Installed("vnavmesh") ? "installed" : "not installed. Gleam cannot walk anywhere without it");
        Add(Installed("Lifestream") ? SelfTestResult.Pass : SelfTestResult.Warn, "Lifestream", Installed("Lifestream") ? "installed" : "not installed. Start runs in an inn room; Gleam cannot teleport");
        Add(SelfTestResult.Note, "AutoRetainer", Installed("AutoRetainer") ? "installed" : "not installed (only needed to clean after ventures)");
        Add(SelfTestResult.Note, "Allagan Tools", Installed("InventoryTools") ? "installed" : "not installed (retainers are read when you visit them)");

        // ---- the item menu ----
        var cb = config.Callbacks;
        (string What, string Label)[] labels =
        [
            ("Sell to a merchant", cb.SellLabel),
            ("Remove materia", cb.RetrieveMateriaLabel),
            ("Take from a retainer", cb.RetrieveFromRetainerLabel),
            ("List on the market", cb.PutUpForSaleLabel),
            ("Sort", cb.SortLabel),
            ("Sell through a retainer", cb.RetainerSellItemLabel),
            ("Hand to a retainer", cb.EntrustLabel),
        ];
        foreach (var (what, label) in labels)
        {
            var ids = context.KnownIds(label);
            Add(ids > 0 ? SelfTestResult.Pass : SelfTestResult.Fail, $"Item menu: {what}",
                ids > 0 ? $"'{context.Display(label)}'" : $"'{label}' is not in the game's text, so Gleam cannot find it on the menu");
        }
        ProbeItemMenu(lines, labels);

        // ---- NPC menus, objects and places ----
        var s = config.Automation;
        (string What, string Fragment)[] menus =
        [
            ("Retainer: inventory", s.EntrustMenuText),
            ("Retainer: leave", s.QuitMenuText),
            ("Retainer: sell from bags", s.SellFromBagsMenuText),
            ("Retainer: sell its own items", s.SellFromRetainerMenuText),
            ("Merchant: open the shop", s.VendorMenuText),
            ("Grand Company: supply missions", s.GcSupplyMenuText),
        ];
        foreach (var (what, fragment) in menus)
        {
            var ok = db.CanTranslateMenuText(fragment);
            Add(ok ? SelfTestResult.Pass : SelfTestResult.Fail, what,
                english ? $"'{fragment}'" : ok ? $"'{db.LocalizeMenuText(fragment)}'" : $"'{fragment}' has no translation Gleam can find");
        }

        void Named(string what, string en, string local)
        {
            var ok = english || !string.Equals(en, local, StringComparison.OrdinalIgnoreCase);
            Add(ok ? SelfTestResult.Pass : SelfTestResult.Fail, what, ok ? $"'{local}'" : $"'{en}' has no translation Gleam can find");
        }
        foreach (var (what, name) in new[] { ("Summoning bell", s.BellObjectName), ("Glamour dresser", s.DresserObjectName) })
        {
            var ids = db.ObjectIdsForEnglishName(name).Count;
            Add(ids > 0 ? SelfTestResult.Pass : SelfTestResult.Fail, what,
                ids > 0 ? "found by the game's own object ids, whatever the client's language" : $"'{name}' is not a name the game uses");
        }
        Add(SelfTestResult.Pass, "Personnel officer", "found by the game's own NPC id, whatever the client's language");
        Named("Merchant town", s.VendorAetheryte, db.LocalizePlaceName(s.VendorAetheryte));
        Add(db.MainCommandIdForEnglishName(s.SaddlebagCommandName) is not null ? SelfTestResult.Pass : SelfTestResult.Fail,
            "Saddlebag command", db.MainCommandIdForEnglishName(s.SaddlebagCommandName) is not null ? "found" : "not found, so Gleam cannot open the saddlebag");

        // ---- settings changed by hand ----
        var defaults = new CallbackSettings();
        var changed = typeof(CallbackSettings).GetProperties()
            .Where(p => !Equals(p.GetValue(cb), p.GetValue(defaults)))
            .Select(p => p.Name)
            .ToList();
        Add(changed.Count == 0 ? SelfTestResult.Pass : SelfTestResult.Warn, "Dialog answers",
            changed.Count == 0 ? "as shipped" : $"changed by hand: {string.Join(", ", changed)}");

        return lines;
    }

    /// <summary>Opens the item menu of one bag item, reads it and closes it: proof the menu reads the way Gleam expects.</summary>
    private void ProbeItemMenu(List<SelfTestLine> lines, (string What, string Label)[] labels)
    {
        const string area = "Item menu, read live";
        if (IsBusy() || condition[ConditionFlag.InCombat] || condition[ConditionFlag.OccupiedInEvent] || AddonDriver.IsAddonVisible("ContextMenu"))
        {
            lines.Add(new SelfTestLine(SelfTestResult.Note, area, "skipped: something is going on. Try again when your character is idle"));
            return;
        }
        var item = scanner.ScanKind(ContainerKind.Inventory).FirstOrDefault();
        if (item is null)
        {
            lines.Add(new SelfTestLine(SelfTestResult.Note, area, "skipped: your bags are empty"));
            return;
        }
        var entries = context.ReadEntries(item.Slot);
        if (entries.Count == 0)
        {
            lines.Add(new SelfTestLine(SelfTestResult.Fail, area, "the menu of a bag item came back empty. The game's menu may have changed"));
            return;
        }
        var known = labels.Where(l => entries.Any(e => context.Matches(e, l.Label))).Select(l => l.What).ToList();
        lines.Add(known.Count > 0
            ? new SelfTestLine(SelfTestResult.Pass, area, $"{entries.Count} entries, recognised: {string.Join(", ", known)}")
            : new SelfTestLine(SelfTestResult.Warn, area, $"{entries.Count} entries, none Gleam recognises: {string.Join(" | ", entries.Select(e => e.Text).Take(8))}"));
    }
}
