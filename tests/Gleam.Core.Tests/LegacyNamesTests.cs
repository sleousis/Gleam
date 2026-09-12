using Gleam.Core.Integrations;
using Gleam.Core.Lists;
using Gleam.Core.Organizer.Model;
using Newtonsoft.Json;

namespace Gleam.Core.Tests;

/// <summary>What a player keeps across the rename from TidyUp: settings, and layouts they shared.</summary>
public class LegacyNamesTests
{
    [Fact]
    public void Settings_saved_under_the_old_name_load_under_the_new_one()
    {
        var settings = new JsonSerializerSettings { TypeNameHandling = TypeNameHandling.Objects };
        var store = new ProfileStore();
        store.Account.ExcludedRetainerIds.Add(0xBEEF);
        var saved = JsonConvert.SerializeObject(store, settings).Replace("Gleam.Core", "TidyUp.Core");
        Assert.Contains("TidyUp.Core.Lists.ProfileStore, TidyUp.Core", saved);
        Assert.ThrowsAny<JsonException>(() => JsonConvert.DeserializeObject<ProfileStore>(saved, settings));

        var back = JsonConvert.DeserializeObject<ProfileStore>(LegacyNames.RewriteConfig(saved), settings);

        Assert.NotNull(back);
        Assert.Contains(0xBEEFul, back!.Account.ExcludedRetainerIds);
    }

    [Fact]
    public void Only_type_markers_change_never_what_the_player_typed()
    {
        const string old = """{"$type":"System.Collections.Generic.List`1[[TidyUp.Core.Organizer.Model.OrganizerRule, TidyUp.Core]], System.Private.CoreLib","Name":"TidyUp corner"}""";

        var now = LegacyNames.RewriteConfig(old);

        Assert.Contains("[[Gleam.Core.Organizer.Model.OrganizerRule, Gleam.Core]]", now);
        Assert.Contains("\"Name\":\"TidyUp corner\"", now);
    }

    [Fact]
    public void Settings_the_old_copy_saved_later_win_and_new_only_settings_are_kept()
    {
        const string old = """{"$type":"TidyUp.Configuration, TidyUp","Version":15,"Junk":{"$type":"TidyUp.Core.Lists.ItemList, TidyUp.Core","Count":8}}""";
        const string current = """{"$type":"Gleam.Configuration, Gleam","Version":17,"Junk":{"$type":"Gleam.Core.Lists.ItemList, Gleam.Core","Count":2},"LastSeenVersion":"0.9.5"}""";

        var merged = System.Text.Json.Nodes.JsonNode.Parse(LegacyNames.MergeConfig(old, current))!.AsObject();

        Assert.Equal("Gleam.Configuration, Gleam", (string?)merged["$type"]);
        Assert.Equal(8, (int)merged["Junk"]!["Count"]!);
        Assert.Equal("Gleam.Core.Lists.ItemList, Gleam.Core", (string?)merged["Junk"]!["$type"]);
        Assert.Equal(15, (int)merged["Version"]!);          // so Gleam's own migrations run over the old settings again
        Assert.Equal("0.9.5", (string?)merged["LastSeenVersion"]);
        Assert.StartsWith("{\n  \"$type\"", LegacyNames.MergeConfig(old, current).Replace("\r", ""));
    }

    [Fact]
    public void Only_history_written_after_gleams_newest_entry_comes_across()
    {
        const string current = "{\"At\":\"2026-09-09T10:00:00+00:00\",\"Item\":1}\n{\"At\":\"2026-09-09T11:00:00+00:00\",\"Item\":2}\n";
        const string old = "{\"At\":\"2026-09-09T10:00:00+00:00\",\"Item\":1}\n{\"At\":\"2026-09-09T11:00:00+00:00\",\"Item\":2}\n"
                           + "not json\n{\"At\":\"2026-09-10T19:44:00+00:00\",\"Item\":3}\r\n{\"At\":\"2026-09-11T22:21:00+00:00\",\"Item\":4}";

        var newer = LegacyNames.NewerLines(old, current);

        Assert.Equal(2, newer.Count);
        Assert.Contains("\"Item\":3", newer[0]);
        Assert.Contains("\"Item\":4", newer[1]);
        Assert.Equal(4, LegacyNames.NewerLines(old, string.Empty).Count);
    }

    [Fact]
    public void Layouts_shared_before_the_rename_still_import()
    {
        var text = OrganizerPlanCodec.Export(OrganizerPlan.Starter());
        Assert.StartsWith("GLEAM1:", text);

        var shared = LegacyNames.LayoutPrefix + text["GLEAM1:".Length..];

        Assert.NotNull(OrganizerPlanCodec.TryImport(shared, []));
    }
}
