using Dalamud.Game.DutyState;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace Gleam.Game;

/// <summary>Server info bar entry: "38/140 · 12 cleanable", click to open, toast when the inventory crosses the fullness threshold.</summary>
public sealed class DtrEntry : IDisposable
{
    private readonly IDtrBar dtr;
    private readonly IToastGui toast;
    private readonly IFramework framework;
    private readonly Action openWindow;
    private IDtrBarEntry? entry;
    private bool nudgedThisCrossing;
    private DateTime lastRefresh = DateTime.MinValue;

    // Counting the bags is the expensive half, so it stays on a two-second poll. What the player reads
    // catches up every frame, which is the difference between a figure that counts and one that steps.
    private int targetFree, targetCleanable, targetPct;
    private double shownFree, shownCleanable;
    private bool haveShown;
    private string lastText = string.Empty;

    public Func<int>? CleanableCount { get; set; }
    public bool Enabled { get; set; } = true;
    public int NudgePercent { get; set; } = 90;

    /// <summary>Right-click opens the organizing half, when the player uses it.</summary>
    public Action? OpenOrganize { get; set; }

    public DtrEntry(IDtrBar dtr, IToastGui toast, IFramework framework, Action openWindow)
    {
        this.dtr = dtr;
        this.toast = toast;
        this.framework = framework;
        this.openWindow = openWindow;
        framework.Update += OnUpdate;
    }

    public void Dispose()
    {
        framework.Update -= OnUpdate;
        entry?.Remove();
        entry = null;
    }

    /// <summary>
    /// A logout or a character switch. The next character's bags load a moment after login and the first
    /// reading can be nonsense, so the fullness nudge waits until it has seen the bags below the line.
    /// </summary>
    public void Reset()
    {
        haveShown = false;
        lastText = string.Empty;
        nudgedThisCrossing = true;
    }

    private void OnUpdate(IFramework f)
    {
        // Hiding the entry no longer silences the fullness nudge: they are two settings on the page, so they
        // are two switches here. Measuring carries on; only the drawing stops.
        if (!Enabled && entry is not null) entry.Shown = false;
        if ((DateTime.UtcNow - lastRefresh).TotalSeconds >= 2) Measure();
        if (Enabled) DrawEntry(f);
    }

    /// <summary>Reads the bags and updates what the entry is counting towards.</summary>
    private void Measure()
    {
        lastRefresh = DateTime.UtcNow;

        var total = GameInventoryScanner.TotalInventorySlots();
        var free = GameInventoryScanner.FreeInventorySlots();
        var used = total - free;
        var pct = total == 0 ? 0 : used * 100 / total;
        var cleanable = CleanableCount?.Invoke() ?? 0;

        entry ??= dtr.Get("Gleam");
        entry.OnClick = e =>
        {
            if (e.ClickType == MouseClickType.Right && OpenOrganize is not null) OpenOrganize();
            else openWindow();
        };
        // Nothing loaded yet means nothing worth saying.
        entry.Shown = Enabled && total > 0;
        if (total == 0) return;

        targetFree = free;
        targetCleanable = cleanable;
        targetPct = pct;
        if (!haveShown) { shownFree = free; shownCleanable = cleanable; haveShown = true; }

        var tip = new SeStringBuilder()
            .AddText($"Bags: {used} of {total} used, {free} free ({pct}%).\n")
            .AddText(cleanable > 0
                ? $"{cleanable} item{(cleanable == 1 ? " looks" : "s look")} like junk.\n"
                : "Nothing looks like junk right now.\n")
            .AddText("Click to open Gleam.");
        if (OpenOrganize is not null) tip.AddText(" Right-click to put things away.");
        entry.Tooltip = tip.Build();

        if (pct >= NudgePercent)
        {
            if (!nudgedThisCrossing)
            {
                nudgedThisCrossing = true;
                if (cleanable > 0)
                    toast.ShowNormal($"Gleam: your bags are {pct}% full. {cleanable} item{(cleanable == 1 ? "" : "s")} could be cleaned. /gleam to review.");
            }
        }
        else if (pct < NudgePercent - 5)
        {
            nudgedThisCrossing = false;
        }
    }

    /// <summary>
    /// Eases the two figures towards what the last count found and rewrites the entry only when a whole
    /// number actually changes, so the info bar is not rebuilt sixty times a second for nothing.
    /// </summary>
    private void DrawEntry(IFramework f)
    {
        if (entry is null || !haveShown) return;

        if (Windows.Ui.Reduced)
        {
            shownFree = targetFree;
            shownCleanable = targetCleanable;
        }
        else
        {
            var dt = Math.Clamp(f.UpdateDelta.TotalSeconds, 0, 0.1);
            var k = 1 - Math.Exp(-6 * dt);
            shownFree += (targetFree - shownFree) * k;
            shownCleanable += (targetCleanable - shownCleanable) * k;
            if (Math.Abs(targetFree - shownFree) < 0.5) shownFree = targetFree;
            if (Math.Abs(targetCleanable - shownCleanable) < 0.5) shownCleanable = targetCleanable;
        }

        var free = (int)Math.Round(shownFree);
        var junk = (int)Math.Round(shownCleanable);
        // Free space is what a player actually wants to know, junk second, and a warning glyph only once
        // the bags are genuinely tight.
        var plain = $"{(targetPct >= NudgePercent ? "!" : "")}{free} free{(junk > 0 ? $" · {junk} junk" : "")}";
        if (plain == lastText) return;
        lastText = plain;

        var text = new SeStringBuilder();
        if (targetPct >= NudgePercent) text.AddIcon(BitmapFontIcon.Warning);
        text.AddText($"{free} free");
        if (junk > 0) text.AddText($" · {junk} junk");
        entry.Text = text.Build();
    }
}

/// <summary>After a duty ends, offer to clean the loot that just landed.</summary>
public sealed class DutyNudge : IDisposable
{
    private readonly IDutyState duty;
    private readonly IFramework framework;
    private readonly Func<Task<int>> scanAndCount;
    private readonly Action<int> notify;

    public bool Enabled { get; set; } = true;

    public DutyNudge(IDutyState duty, IFramework framework, Func<Task<int>> scanAndCount, Action<int> notify)
    {
        this.duty = duty;
        this.framework = framework;
        this.scanAndCount = scanAndCount;
        this.notify = notify;
        duty.DutyCompleted += OnCompleted;
    }

    private bool disposed;

    public void Dispose()
    {
        disposed = true;
        duty.DutyCompleted -= OnCompleted;
    }

    private void OnCompleted(IDutyStateEventArgs args)
    {
        if (!Enabled) return;
        // Loot lands a few seconds after the completion flag; give it time.
        framework.RunOnTick(async () =>
        {
            // Queued six seconds ago; the plugin may have been unloaded since.
            if (disposed) return;
            try
            {
                var count = await scanAndCount().ConfigureAwait(false);
                if (count > 0) notify(count);
            }
            catch
            {
                // A nudge must never surface an error.
            }
        }, delay: TimeSpan.FromSeconds(6));
    }
}
