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
    public void Layouts_shared_before_the_rename_still_import()
    {
        var text = OrganizerPlanCodec.Export(OrganizerPlan.Starter());
        Assert.StartsWith("GLEAM1:", text);

        var shared = LegacyNames.LayoutPrefix + text["GLEAM1:".Length..];

        Assert.NotNull(OrganizerPlanCodec.TryImport(shared, []));
    }
}
