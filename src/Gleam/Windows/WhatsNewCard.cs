using Dalamud.Bindings.ImGui;
using Gleam.Core.Integrations;

namespace Gleam.Windows;

/// <summary>
/// "What's new", shown once after an update, at the top of Gleam's own window.
///
/// Dalamud shows the changelog beside an update in its installer, but most players let plugins update at
/// startup and never open it. This is a card rather than a window of its own: it waits until the player
/// opens Gleam anyway, and one click puts it away. It reads the changelog built into the plugin, the same
/// text the installer and the release page show. A fresh install never sees it; the first-run screen
/// already says what Gleam is.
/// </summary>
public sealed class WhatsNewCard
{
    private const string ChangelogUrl = "https://github.com/sleousis/Gleam/blob/main/CHANGELOG.md";

    private readonly Configuration config;
    private readonly Version current = typeof(Plugin).Assembly.GetName().Version ?? new Version(0, 0);
    private IReadOnlyList<ReleaseNote>? unseen;

    public WhatsNewCard(Configuration config) => this.config = config;

    private string CurrentText => current.ToString(3);

    private IReadOnlyList<ReleaseNote> Unseen => unseen ??= Load();

    private IReadOnlyList<ReleaseNote> Load()
    {
        try
        {
            using var stream = typeof(Plugin).Assembly.GetManifestResourceStream("Gleam.CHANGELOG.md");
            if (stream is null) return Array.Empty<ReleaseNote>();
            using var reader = new StreamReader(stream);
            return ReleaseNotes.Since(ReleaseNotes.Parse(reader.ReadToEnd()), config.LastSeenVersion, current);
        }
        catch
        {
            // Release notes are a courtesy; a broken one must never stand between the player and the list.
            return Array.Empty<ReleaseNote>();
        }
    }

    public void Draw()
    {
        if (!config.SeenFirstRun) return;
        // No version seen and the first run already done: a fresh install. Start counting from here.
        if (config.LastSeenVersion is null) { Dismiss(); return; }
        var notes = Unseen;
        if (notes.Count == 0) return;

        var newest = notes[0];
        using (Ui.Card("whatsnew"))
        {
            Ui.Ask($"What's new in Gleam {newest.Version.ToString(3)}");
            foreach (var point in newest.Points)
            {
                ImGui.Bullet();
                ImGui.SameLine();
                ImGui.TextWrapped(point);
            }
            if (notes.Count > 1)
                Ui.Hint($"There {(notes.Count == 2 ? "was one more update" : $"were {notes.Count - 1} more updates")} since you last looked.");
            Ui.Gap(0.3f);
            if (Ui.PrimaryButton("Got it", 120 * Ui.Scale)) Dismiss();
            ImGui.SameLine();
            ImGui.AlignTextToFramePadding();
            if (Ui.LinkButton("Every change")) Ui.OpenLink(ChangelogUrl);
        }
        Ui.Gap(0.4f);
    }

    private void Dismiss()
    {
        config.LastSeenVersion = CurrentText;
        config.Save(PluginServices.PluginInterface);
        unseen = Array.Empty<ReleaseNote>();
    }
}
