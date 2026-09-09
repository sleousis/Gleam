using Dalamud.Game.DutyState;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Plugin.Services;

namespace TidyUp.Game;

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

    private void OnUpdate(IFramework f)
    {
        if (!Enabled)
        {
            if (entry is not null) { entry.Shown = false; }
            return;
        }
        if ((DateTime.UtcNow - lastRefresh).TotalSeconds < 2) return;
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
        entry.Shown = total > 0;
        if (total == 0) return;

        // Free space is what a player actually wants to know, junk second, and a warning glyph only once
        // the bags are genuinely tight.
        var text = new SeStringBuilder();
        if (pct >= NudgePercent) text.AddIcon(BitmapFontIcon.Warning);
        text.AddText($"{free} free");
        if (cleanable > 0) text.AddText($" · {cleanable} junk");
        entry.Text = text.Build();

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

    public void Dispose() => duty.DutyCompleted -= OnCompleted;

    private void OnCompleted(IDutyStateEventArgs args)
    {
        if (!Enabled) return;
        // Loot lands a few seconds after the completion flag; give it time.
        framework.RunOnTick(async () =>
        {
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
