using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TidyUp.Game;

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
        public string? ExpectedSubstring;
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
    public Task<bool> ExpectAsync(string addonName, int callbackValue, string? expectedSubstring, TimeSpan timeout, CancellationToken ct)
    {
        LastRejection = null;
        if (framework.IsInFrameworkUpdateThread ? IsAddonVisible(addonName) : framework.RunOnFrameworkThread(() => IsAddonVisible(addonName)).Result)
        {
            LastRejection = "a game window that Tidy Up needs to answer is already open; close it first";
            return Task.FromResult(false);
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            armed?.Completion.TrySetResult(false);
            armed = new Armed { AddonName = addonName, CallbackValue = callbackValue, ExpectedSubstring = expectedSubstring, Completion = tcs };
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
        if (addon == null || !addon->IsVisible) return;

        if (a.AddonName == "SelectYesno" && a.ExpectedSubstring is not null)
        {
            var prompt = ReadYesNoPrompt(addon);
            // Item names carry soft hyphens and the prompt carries payload bytes; compare letters and digits only.
            if (prompt is null || !PromptMentions(prompt, a.ExpectedSubstring))
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

    /// <summary>Letters and digits only, lower-cased: immune to soft hyphens, payload bytes, and punctuation.</summary>
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

    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }

    /// <summary>
    /// Whether a confirmation prompt is about the named item. The game pluralises stacks ("Discard 986 magicked
    /// prisms (sunshine)?"), so every word of the name must appear in the prompt either as is or with a plural
    /// ending; the quantity and punctuation are ignored.
    /// </summary>
    public static bool PromptMentions(string prompt, string itemName)
    {
        var flat = Normalize(prompt);
        if (flat.Contains(Normalize(itemName), StringComparison.OrdinalIgnoreCase)) return true;

        var promptWords = Words(prompt);
        foreach (var word in Words(itemName))
        {
            if (promptWords.Contains(word)) continue;
            if (promptWords.Contains(word + "s") || promptWords.Contains(word + "es")) continue;
            if (word.EndsWith('y') && promptWords.Contains(word[..^1] + "ies")) continue;
            if (word.EndsWith('f') && promptWords.Contains(word[..^1] + "ves")) continue;
            if (word.EndsWith("fe") && promptWords.Contains(word[..^2] + "ves")) continue;
            if (word.EndsWith("us") && promptWords.Contains(word[..^2] + "i")) continue;
            return false;
        }
        return true;
    }

    private static HashSet<string> Words(string s)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        var sb = new System.Text.StringBuilder();
        foreach (var ch in s + " ")
        {
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            else if (sb.Length > 0) { set.Add(sb.ToString()); sb.Clear(); }
        }
        // "of", "the", "a" appear in prompts at will; never require them.
        set.Remove("of"); set.Remove("the"); set.Remove("a"); set.Remove("an");
        return set;
    }

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

    public static bool IsAddonVisible(string name)
    {
        var addon = GetAddon(name);
        return addon != null && addon->IsVisible;
    }

    public static AtkUnitBase* GetAddon(string name) =>
        (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
}
