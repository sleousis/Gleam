using Newtonsoft.Json;
using TidyUp.Core.Lists;
using TidyUp.Core.Model;
using TidyUp.Core.Rules;

namespace TidyUp.Core.Tests;

/// <summary>
/// Loads saved settings the way Dalamud does. Its loader fills existing objects and collections in place
/// instead of replacing them, which has now cost data three times: a shared default mutated, rules doubled
/// on every start, and turned-off junk rules switching themselves back on.
/// </summary>
public class ConfigLoadTests
{
    private static readonly JsonSerializerSettings Dalamud = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
    };

    private static T Restart<T>(T saved)
    {
        var json = JsonConvert.SerializeObject(saved, Formatting.Indented, Dalamud);
        return JsonConvert.DeserializeObject<T>(json, Dalamud)!;
    }

    [Fact]
    public void A_junk_rule_turned_off_stays_off_after_a_restart()
    {
        var store = new ProfileStore();
        store.Account.EnabledRules.Remove(ObsoleteGearRule.RuleId);

        var loaded = Restart(store);

        Assert.DoesNotContain(ObsoleteGearRule.RuleId, loaded.Account.EnabledRules);
        Assert.Equal(store.Account.EnabledRules.Count, loaded.Account.EnabledRules.Count);
    }

    [Fact]
    public void Every_rule_turned_off_stays_off()
    {
        var store = new ProfileStore();
        store.Account.EnabledRules.Clear();

        Assert.Empty(Restart(store).Account.EnabledRules);
    }

    [Fact]
    public void Loading_into_a_profile_that_already_exists_replaces_its_rules()
    {
        // The configuration's profile store is already constructed when loading starts, so the loader fills
        // that one rather than making a new one. That is the path the bug took.
        var saved = new Profile();
        saved.EnabledRules.Remove(VendorOnlyJunkRule.RuleId);
        var target = new Profile();

        JsonConvert.PopulateObject(JsonConvert.SerializeObject(saved, Dalamud), target, Dalamud);

        Assert.DoesNotContain(VendorOnlyJunkRule.RuleId, target.EnabledRules);
    }

    [Fact]
    public void The_rest_of_a_profile_survives_a_restart()
    {
        var store = new ProfileStore();
        store.Account.ContainerEnabled[ContainerKind.Saddlebag] = false;
        store.Account.AutoOpenOnContainer[ContainerKind.Retainer] = true;
        store.Account.ExcludedRetainerIds.Add(42);
        store.Account.LargeStackGuard = 7;

        var loaded = Restart(store).Account;

        Assert.False(loaded.IsContainerEnabled(ContainerKind.Saddlebag));
        Assert.True(loaded.IsAutoOpen(ContainerKind.Retainer));
        Assert.Equal([42ul], loaded.ExcludedRetainerIds);
        Assert.Equal(7, loaded.LargeStackGuard);
    }
}
