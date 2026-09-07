using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace TidyUp.Game;

/// <summary>
/// Waits for named native dialogs and answers them. Armed per action: a SelectYesno that appears
/// while nothing is armed is left strictly alone, and one whose prompt does not mention the expected
/// item name is left alone too.
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

    /// <summary>Arms a one-shot answer for the next appearance of <paramref name="addonName"/>.</summary>
    public Task<bool> ExpectAsync(string addonName, int callbackValue, string? expectedSubstring, TimeSpan timeout, CancellationToken ct)
    {
        LastRejection = null;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate)
        {
            armed?.Completion.TrySetResult(false);
            armed = new Armed { AddonName = addonName, CallbackValue = callbackValue, ExpectedSubstring = expectedSubstring, Completion = tcs };
        }

        // If the dialog is already up (fast game), answer it now.
        framework.RunOnFrameworkThread(() => TryAnswerExisting(addonName));

        var timeoutTask = Task.Delay(timeout, ct);
        return Task.WhenAny(tcs.Task, timeoutTask).ContinueWith(t =>
        {
            lock (gate) { if (armed?.Completion == tcs) armed = null; }
            return tcs.Task.IsCompletedSuccessfully && tcs.Task.Result;
        }, ct);
    }

    public void Disarm()
    {
        lock (gate)
        {
            armed?.Completion.TrySetResult(false);
            armed = null;
        }
    }

    private void TryAnswerExisting(string addonName)
    {
        var addon = (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName(addonName, 1);
        if (addon == null || !addon->IsVisible) return;
        Answer(addonName, addon);
    }

    private void OnDialog(AddonEvent type, AddonArgs args)
    {
        Armed? a;
        lock (gate) a = armed;
        if (a is null || !string.Equals(a.AddonName, args.AddonName, StringComparison.Ordinal)) return;
        // PostSetup fires before values settle for some dialogs; answer on the next tick.
        framework.RunOnTick(() => TryAnswerExisting(a.AddonName), delayTicks: 1);
    }

    private void Answer(string addonName, AtkUnitBase* addon)
    {
        Armed? a;
        lock (gate) a = armed;
        if (a is null || a.AddonName != addonName) return;

        if (addonName == "SelectYesno" && a.ExpectedSubstring is not null)
        {
            var prompt = ReadYesNoPrompt(addon);
            // Item names carry soft hyphens and the prompt carries payload bytes; compare letters and digits only.
            if (prompt is not null && !Normalize(prompt).Contains(Normalize(a.ExpectedSubstring), StringComparison.OrdinalIgnoreCase))
            {
                LastRejection = $"SelectYesno prompt '{prompt}' does not mention '{a.ExpectedSubstring}'; dialog left open";
                log.Warning("{Rejection}", LastRejection);
                lock (gate) { if (armed == a) armed = null; }
                a.Completion.TrySetResult(false);
                return;
            }
        }

        LastRejection = null;
        log.Debug("Answering {Addon} with callback {Value}", addonName, a.CallbackValue);
        var ok = addon->FireCallbackInt(a.CallbackValue);
        lock (gate) { if (armed == a) armed = null; }
        a.Completion.TrySetResult(ok);
    }

    /// <summary>Why the last armed dialog was refused, for the spike window.</summary>
    public string? LastRejection { get; private set; }

    /// <summary>Letters and digits only, lower-cased: immune to soft hyphens, payload bytes, and punctuation.</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
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
        var addon = (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
        return addon != null && addon->IsVisible;
    }

    public static AtkUnitBase* GetAddon(string name) =>
        (AtkUnitBase*)RaptureAtkUnitManager.Instance()->GetAddonByName(name, 1);
}
