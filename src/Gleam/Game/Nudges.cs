using Dalamud.Game.DutyState;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace Gleam.Game;

/// <summary>
/// Server info bar entry: "Bags 18/140 · 3 junk", the way the game's own inventory counter reads. Click to
/// open, toast when the inventory crosses the fullness threshold.
/// </summary>
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
    private int targetUsed, targetTotal, targetCleanable, targetPct;
    private double shownUsed, shownCleanable;
    private bool haveShown;

    // What the entry and its tooltip last said, kept as the numbers they were made from. Each is rebuilt only
    // when one of its numbers changes, so nothing is formatted on a frame where nothing moved.
    private static readonly (int, int, int, bool) NothingShown = (-1, -1, -1, false);
    private (int Used, int Total, int Junk, bool Warn) shownText = NothingShown;
    private (int Used, int Total, int Junk, bool ShowsJunk, bool Organize) shownTip = (-1, -1, -1, false, false);

    public Func<int>? CleanableCount { get; set; }

    /// <summary>False when the player has turned clearing junk off: the tooltip then says nothing about junk.</summary>
    public Func<bool>? ShowsJunk { get; set; }
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
        shownText = NothingShown;
        shownTip = (-1, -1, -1, false, false);
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

        if (entry is null)
        {
            entry = dtr.Get("Gleam");
            entry.OnClick = e =>
            {
                if (e.ClickType == MouseClickType.Right && OpenOrganize is not null) OpenOrganize();
                else openWindow();
            };
        }
        // Nothing loaded yet means nothing worth saying.
        entry.Shown = Enabled && total > 0;
        if (total == 0) return;

        targetUsed = used;
        targetTotal = total;
        targetCleanable = cleanable;
        targetPct = pct;
        if (!haveShown) { shownUsed = used; shownCleanable = cleanable; haveShown = true; }

        var showsJunk = ShowsJunk?.Invoke() ?? true;
        var tipFrom = (used, total, cleanable, showsJunk, OpenOrganize is not null);
        if (tipFrom != shownTip)
        {
            shownTip = tipFrom;
            var tip = new SeStringBuilder().AddText($"{used} of {total} bag slots used, {free} free");
            if (showsJunk)
                tip.AddText(cleanable == 0 ? "\nNothing to clean right now"
                    : cleanable == 1 ? "\n1 item looks like junk"
                    : $"\n{cleanable} items look like junk");
            tip.AddText("\n\nClick to review and clean");
            if (OpenOrganize is not null) tip.AddText("\nRight-click to put things away");
            entry.Tooltip = tip.Build();
        }

        if (pct >= NudgePercent)
        {
            if (!nudgedThisCrossing)
            {
                nudgedThisCrossing = true;
                if (cleanable > 0)
                    toast.ShowNormal($"Your bags are {pct}% full, and Gleam found {cleanable} item{(cleanable == 1 ? "" : "s")} to clean. Type /gleam to review {(cleanable == 1 ? "it" : "them")}.");
            }
        }
        else if (pct < NudgePercent - 5)
        {
            nudgedThisCrossing = false;
        }
    }

    /// <summary>
    /// Eases the two figures towards what the last count found and rewrites the entry only when a whole
    /// number or the warning actually changes. The numbers are compared first, so a frame where nothing
    /// moved formats nothing at all.
    /// </summary>
    private void DrawEntry(IFramework f)
    {
        if (entry is null || !haveShown) return;

        if (Windows.Ui.Reduced)
        {
            shownUsed = targetUsed;
            shownCleanable = targetCleanable;
        }
        else
        {
            var dt = Math.Clamp(f.UpdateDelta.TotalSeconds, 0, 0.1);
            var k = 1 - Math.Exp(-6 * dt);
            shownUsed += (targetUsed - shownUsed) * k;
            shownCleanable += (targetCleanable - shownCleanable) * k;
            if (Math.Abs(targetUsed - shownUsed) < 0.5) shownUsed = targetUsed;
            if (Math.Abs(targetCleanable - shownCleanable) < 0.5) shownCleanable = targetCleanable;
        }

        // Bag use first, read like the game's own counter, junk second, and a warning glyph only once the
        // bags are genuinely tight.
        var now = ((int)Math.Round(shownUsed), targetTotal, (int)Math.Round(shownCleanable), targetPct >= NudgePercent);
        if (now == shownText) return;
        shownText = now;
        var (used, total, junk, warn) = now;

        var text = new SeStringBuilder();
        if (warn) text.AddIcon(BitmapFontIcon.Warning);
        text.AddText($"Bags {used}/{total}");
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
