using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace Gleam.Game;

/// <summary>
/// Answers named native dialogs, but only ones that appear *after* an action armed it, and for
/// SelectYesno only when the prompt names the expected item. A dialog that is already open when an
/// action starts is never touched: it belongs to the player, not to us.
/// </summary>
public sealed unsafe class AddonDriver : IDisposable
{
    private readonly IAddonLifecycle lifecycle;
    private readonly IFramework framework;
    private readonly IPluginLog log;
    private readonly object gate = new();

    private sealed class Armed
    {
        public required string AddonName;
        public required int CallbackValue;
        /// <summary>Any one of these must appear in a yes/no prompt: the item's names in the client's language.</summary>
        public IReadOnlyList<string>? ExpectedNames;
        public required TaskCompletionSource<bool> Completion;
    }

    private Armed? armed;

    public static readonly string[] WatchedDialogs =
    [
        "SelectYesno", "MateriaRetrieveDialog", "SalvageDialog", "GrandCompanySupplyReward",
    ];

    public AddonDriver(IAddonLifecycle lifecycle, IFramework framework, IPluginLog log)
    {
        this.lifecycle = lifecycle;
        this.framework = framework;
        this.log = log;
        lifecycle.RegisterListener(AddonEvent.PostSetup, WatchedDialogs, OnDialog);
    }

    public void Dispose() => lifecycle.UnregisterListener(AddonEvent.PostSetup, WatchedDialogs, OnDialog);

    /// <summary>Why the last armed dialog was refused or never answered, for the spike window.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>
    /// Arms a one-shot answer for the *next* appearance of <paramref name="addonName"/>. Call this before
    /// the native action so the dialog cannot slip in first. Returns immediately-false if that dialog is
    /// already open, because then it is not ours to answer.
    /// </summary>
    public Task<bool> ExpectAsync(string addonName, int callbackValue, IReadOnlyList<string>? expectedNames, TimeSpan timeout, CancellationToken ct)
    {
        LastRejection = null;
        if (framework.IsInFrameworkUpdateThread ? IsAddonVisible(addonName) : framework.RunOnFrameworkThread(() => IsAddonVisible(addonName)).Result)
        {
            LastRejection = "a game window that Gleam needs to answer is already open. Close it first";
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            armed?.Completion.TrySetResult(false);
            armed = new Armed { AddonName = addonName, CallbackValue = callbackValue, ExpectedNames = expectedNames, Completion = tcs };
        }

        var timeoutTask = Task.Delay(timeout, ct);
        return Task.WhenAny(tcs.Task, timeoutTask).ContinueWith(_ =>
        {
            lock (gate) { if (armed?.Completion == tcs) armed = null; }
            var ok = tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
            if (!ok && LastRejection is null) LastRejection = "the game's confirmation did not appear in time";
            return ok;
        }, CancellationToken.None);
    }

    public void Disarm()
    {
        lock (gate)
        {
            armed?.Completion.TrySetResult(false);
            armed = null;
        }
    }

    private void OnDialog(AddonEvent type, AddonArgs args)
    {
        Armed? a;
        lock (gate) a = armed;
        if (a is null || !string.Equals(a.AddonName, args.AddonName, StringComparison.Ordinal)) return;
        // PostSetup fires before values settle for some dialogs; answer on the next tick.
        framework.RunOnTick(() => Answer(a), delayTicks: 1);
    }

    private void Answer(Armed a)
    {
        lock (gate) { if (armed != a) return; }
        var addon = GetAddon(a.AddonName);
        if (addon == null || !addon->IsVisible)
        {
            // Gone within a frame of opening: another plugin (YesAlready, TextAdvance) answered it first. Every action
            // checks its own result afterwards (the item leaving, the listing appearing), so this counts as answered.
            // It used to wait out the whole timeout and report an item that had been handed in as failed.
            log.Debug("{Addon} was answered before Gleam could", a.AddonName);
            lock (gate) { if (armed == a) armed = null; }
            a.Completion.TrySetResult(true);
            return;
        }

        if (a.AddonName == "SelectYesno" && a.ExpectedNames is { Count: > 0 } names)
        {
            var prompt = ReadYesNoPrompt(addon);
            // Item names carry soft hyphens and the prompt carries payload bytes; compare letters and digits only.
            if (prompt is null || !names.Any(n => PromptMentions(prompt, n)))
            {
                LastRejection = "the confirmation that appeared was about a different item, so it was left alone";
                log.Warning("{Rejection}", LastRejection);
                lock (gate) { if (armed == a) armed = null; }
                a.Completion.TrySetResult(false);
                return;
            }
        }

        log.Debug("Answering {Addon} with callback {Value}", a.AddonName, a.CallbackValue);
        var ok = addon->FireCallbackInt(a.CallbackValue);
        lock (gate) { if (armed == a) armed = null; }
        a.Completion.TrySetResult(ok);
    }

    /// <summary>Closes a named addon if it is up. Cosmetic; never throws.</summary>
    public static void CloseAddon(string name)
    {
        try
        {
            var addon = GetAddon(name);
            if (addon != null && addon->IsVisible) addon->Close(true);
        }
        catch
        {
            // closing is best-effort
        }
    }

    /// <summary>Letters and digits only, lower-cased: immune to soft hyphens, payload bytes, and punctuation.</summary>
    public static string Normalize(string s) => Core.Execution.PromptMatch.Normalize(s);

    /// <summary>Whether a confirmation prompt is about the named item. The rules are with <see cref="Core.Execution.PromptMatch"/>.</summary>
    public static bool PromptMentions(string prompt, string itemName) => Core.Execution.PromptMatch.Mentions(prompt, itemName);

    private static string? ReadYesNoPrompt(AtkUnitBase* addon)
    {
        try
        {
            var yesno = (AddonSelectYesno*)addon;
            if (yesno->PromptText == null) return null;
            return yesno->PromptText->NodeText.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The question a visible yes/no prompt asks, or null when none is up. Framework thread only. Both places the
    /// game keeps it are read: the text node, and the addon's first value. In 0.11.3 only the node was read, and
    /// it did not yet hold the retainer's leave question when that appeared, so the question went unrecognised.
    /// </summary>
    public static string? YesNoPromptText()
    {
        var addon = GetAddon("SelectYesno");
        if (addon == null || !addon->IsVisible) return null;
        string? value = null;
        try
        {
            foreach (var v in new Dalamud.Game.NativeWrapper.AtkUnitBasePtr((nint)addon).AtkValues)
            {
                // Whatever type Dalamud boxes the text in, its text is what matters.
                value = v.GetValue()?.ToString();
                break;
            }
        }
        catch
        {
            // the text node below is the fallback
        }
        var parts = new[] { ReadYesNoPrompt(addon), value }.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
        return parts.Count == 0 ? null : string.Join(" ", parts);
    }

    /// <summary>
    /// For "/gleam probe yesno": every way of reading the open yes/no, and with <paramref name="answer"/> whether
    /// pressing Yes works. It answers only a question that mentions buyback. Framework thread only.
    /// </summary>
    public static IReadOnlyList<string> ProbeYesNo(bool answer, int yesCallback, Func<string, bool> recognised)
    {
        var lines = new List<string>();
        var addon = GetAddon("SelectYesno");
        if (addon == null)
        {
            lines.Add("No yes/no window exists.");
            return lines;
        }
        lines.Add($"Yes/no window: visible {addon->IsVisible}, ready {addon->IsReady}, {addon->AtkValuesCount} values.");
        var texts = new List<string>();
        var node = ReadYesNoPrompt(addon);
        if (node is not null) texts.Add(node);
        lines.Add($"Text node: {Quote(node)}");
        try
        {
            var i = 0;
            foreach (var v in new Dalamud.Game.NativeWrapper.AtkUnitBasePtr((nint)addon).AtkValues)
            {
                if (i >= 3) break;
                string type;
                string? text;
                try
                {
                    var value = v.GetValue();
                    type = value?.GetType().Name ?? "null";
                    text = value?.ToString();
                }
                catch (Exception ex)
                {
                    type = ex.GetType().Name;
                    text = null;
                }
                if (text is not null) texts.Add(text);
                lines.Add($"Value {i}: {type} {Quote(text)}");
                i++;
            }
        }
        catch (Exception ex)
        {
            lines.Add($"Values could not be read: {ex.GetType().Name}: {ex.Message}");
        }
        var read = YesNoPromptText();
        lines.Add($"Gleam reads {Quote(read)}, which {(read is not null && recognised(read) ? "is" : "is not")} the buyback question.");
        if (answer)
        {
            if (!texts.Any(t => Normalize(t).Contains("buyback", StringComparison.Ordinal)))
            {
                lines.Add("Not answered: nothing read from it mentions buyback.");
            }
            else
            {
                var values = stackalloc AtkValue[1];
                values[0].SetInt(yesCallback);
                lines.Add($"Pressed Yes, updating the window's state as a click does: the callback returned {addon->FireCallback(1, values, true)}.");
            }
        }
        return lines;
    }

    private static string Quote(string? s) => s is null ? "(nothing)" : $"\"{(s.Length > 120 ? s[..120] + "..." : s)}\"";

    public static bool IsAddonVisible(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    public static AtkUnitBase* GetAddon(string name) =>
        (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
}
