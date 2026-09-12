using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Gleam.Core.Lists;
using Gleam.Core.Model;
using Gleam.Core.Organizer.Model;
using Gleam.Core.Rules;
using Gleam.Core.Settings;

namespace Gleam.Core.Tests;

/// <summary>
/// The plugin's settings as Dalamud saves them, for tests that cannot load the plugin itself. Same member names,
/// nesting and defaults as src/Gleam/Configuration.cs for everything the migration steps touch, and the same
/// explicit members for what differs. Keep the two in step when that file changes.
/// </summary>
public sealed class SavedSettings : IMigratableSettings
{
    public int Version { get; set; } = ConfigMigrations.CurrentVersion;
    public ProfileStore Profiles { get; set; } = new();
    public ItemList ProtectList { get; set; } = new();
    public ItemList AlwaysDiscardList { get; set; } = new();
    public SavedCallbacks Callbacks { get; set; } = new();
    public SavedAutomation Automation { get; set; } = new();
    public SavedOrganizer Organizer { get; set; } = new();
    public bool ActWhenMateriaFails { get; set; }
    public bool SeenFirstRun { get; set; }
    public bool AnsweredOrganizeOffer { get; set; }
    public bool UseClean { get; set; } = true;
    public bool UseOrganize { get; set; }
    public bool AdvancedMode { get; set; }
    public bool UseUniversalis { get; set; } = true;
    public bool UseAllaganTools { get; set; } = true;
    public string? LastSeenVersion { get; set; }

    IMigratableAutomation IMigratableSettings.Automation => Automation;
    IMigratableLayouts IMigratableSettings.Organizer => Organizer;
    int IMigratableSettings.ActionTimeoutMs
    {
        get => Callbacks.ActionTimeoutMs;
        set => Callbacks.ActionTimeoutMs = value;
    }
}

public sealed class SavedCallbacks
{
    public int ActionTimeoutMs { get; set; } = 8000;
    public int RateLimitMs { get; set; } = 250;
}

public sealed class SavedAutomation : IMigratableAutomation
{
    public UnseenRowsMode UnseenRows { get; set; } = UnseenRowsMode.Clean;
    public bool VisitContainersWithoutRows { get; set; } = false;
    public bool Enabled { get; set; } = true;
    public bool VisitRetainers { get; set; } = true;
    public bool SellAtVendor { get; set; } = true;
    public bool VisitDresser { get; set; } = true;
    public bool OpenSaddlebag { get; set; } = true;
    public bool VisitGrandCompany { get; set; } = true;
}

public sealed class SavedOrganizer : IMigratableLayouts
{
    public List<OrganizerPlan> Plans { get; set; } = new();
    public Guid? ActivePlanId { get; set; }
    public Guid? AdvancedPlanId { get; set; }
}

/// <summary>
/// Settings files written by older builds, run through the migration chain. Each file is loaded the way Dalamud
/// loads it, <c>$type</c> names included, so a step that forgets an old shape or a type that moves shows up here.
/// </summary>
public class ConfigMigrationTests
{
    /// <summary>Maps the plugin's own type names in a saved file onto the stand-ins above, both ways.</summary>
    private sealed class PluginTypes : DefaultSerializationBinder
    {
        private static readonly Dictionary<string, Type> ByName = new()
        {
            ["Gleam.Configuration"] = typeof(SavedSettings),
            ["Gleam.CallbackSettings"] = typeof(SavedCallbacks),
            ["Gleam.AutomationSettings"] = typeof(SavedAutomation),
            ["Gleam.OrganizerSettings"] = typeof(SavedOrganizer),
        };

        public override Type BindToType(string? assemblyName, string typeName) =>
            assemblyName == "Gleam" && ByName.TryGetValue(typeName, out var t) ? t : base.BindToType(assemblyName, typeName);

        public override void BindToName(Type serializedType, out string? assemblyName, out string? typeName)
        {
            var plugin = ByName.FirstOrDefault(kv => kv.Value == serializedType).Key;
            if (plugin is not null) { assemblyName = "Gleam"; typeName = plugin; return; }
            base.BindToName(serializedType, out assemblyName, out typeName);
        }
    }

    private static readonly JsonSerializerSettings Dalamud = new()
    {
        TypeNameAssemblyFormatHandling = TypeNameAssemblyFormatHandling.Simple,
        TypeNameHandling = TypeNameHandling.Objects,
        SerializationBinder = new PluginTypes(),
    };

    private static SavedSettings Load(string json) => JsonConvert.DeserializeObject<SavedSettings>(json, Dalamud)!;
    private static string Save(SavedSettings s) => JsonConvert.SerializeObject(s, Formatting.Indented, Dalamud);

    private static readonly Guid MyLayout = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid GearLayout = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid CrystalsRule = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private static readonly Guid DyesRule = Guid.Parse("d4d4d4d4-0000-0000-0000-000000000004");
    private static readonly Guid SetPiecesRule = Guid.Parse("e5e5e5e5-0000-0000-0000-000000000005");

    // ---------- files as older builds wrote them ----------

    /// <summary>From before layouts existed: the Cautious preset, a short timeout, and hands-free steps turned off.</summary>
    private const string Version2 = """
    {
      "$type": "Gleam.Configuration, Gleam",
      "Version": 2,
      "Profiles": {
        "$type": "Gleam.Core.Lists.ProfileStore, Gleam.Core",
        "Account": {
          "$type": "Gleam.Core.Lists.Profile, Gleam.Core",
          "Preset": 3,
          "Thresholds": { "$type": "Gleam.Core.Rules.Thresholds, Gleam.Core", "Policy": 1, "ObsoleteGearLevelGap": 15 },
          "EnabledRules": [ "vendor-only-junk", "obsolete-gear" ],
          "ExcludedRetainerIds": [ 42 ],
          "LargeStackGuard": 50
        },
        "Overrides": [
          {
            "$type": "Gleam.Core.Lists.CharacterOverride, Gleam.Core",
            "CharacterId": 7,
            "CharacterName": "An Alt",
            "OverriddenProperties": [ "Thresholds" ],
            "Values": { "$type": "Gleam.Core.Lists.Profile, Gleam.Core", "Thresholds": { "Policy": 1 }, "EnabledRules": [] }
          }
        ]
      },
      "ProtectList": {
        "$type": "Gleam.Core.Lists.ItemList, Gleam.Core",
        "Entries": [ { "$type": "Gleam.Core.Lists.ItemListEntry, Gleam.Core", "ItemId": 5, "IncludeHq": true, "Note": "my glamour" } ]
      },
      "AlwaysDiscardList": { "$type": "Gleam.Core.Lists.ItemList, Gleam.Core", "Entries": [ { "ItemId": 7, "IncludeHq": false } ] },
      "Callbacks": { "$type": "Gleam.CallbackSettings, Gleam", "ActionTimeoutMs": 3000, "RateLimitMs": 400 },
      "Automation": {
        "$type": "Gleam.AutomationSettings, Gleam",
        "UnseenRows": 1,
        "VisitContainersWithoutRows": true,
        "Enabled": false,
        "VisitRetainers": false,
        "SellAtVendor": false,
        "VisitDresser": false,
        "OpenSaddlebag": false,
        "VisitGrandCompany": false
      },
      "UseUniversalis": false,
      "UseAllaganTools": false,
      "ActWhenMateriaFails": true
    }
    """;

    /// <summary>
    /// Layouts from 0.7: one still carrying the retired "Gear I can wear" rule and a doubled rule, one already
    /// on gear sets, and one saved with the first one's id.
    /// </summary>
    private const string Version7 = """
    {
      "$type": "Gleam.Configuration, Gleam",
      "Version": 7,
      "Organizer": {
        "$type": "Gleam.OrganizerSettings, Gleam",
        "ActivePlanId": "b2b2b2b2-0000-0000-0000-000000000002",
        "Plans": [
          {
            "$type": "Gleam.Core.Organizer.Model.OrganizerPlan, Gleam.Core",
            "Id": "a1a1a1a1-0000-0000-0000-000000000001",
            "Name": "My layout",
            "Rules": [
              {
                "$type": "Gleam.Core.Organizer.Model.OrganizerRule, Gleam.Core",
                "Id": "c3c3c3c3-0000-0000-0000-000000000003",
                "Name": "Crystals to the saddlebag",
                "Enabled": true,
                "When": { "$type": "Gleam.Core.Organizer.Model.OrganizerPredicate, Gleam.Core", "Tags": [ 4 ] },
                "Then": { "$type": "Gleam.Core.Organizer.Model.Destination, Gleam.Core", "Kind": 3, "RetainerId": 0 }
              },
              { "Id": "f6f6f6f6-0000-0000-0000-000000000006", "Name": "Gear I can wear in the armoury", "When": { "Tags": [ 0 ], "ForJobsPlayed": true }, "Then": { "Kind": 2 } },
              { "Id": "d4d4d4d4-0000-0000-0000-000000000004", "Name": "Dyes in the bags", "When": { "UiCategories": [ "Dye" ] }, "Then": { "Kind": 1 } },
              { "Id": "d4d4d4d4-0000-0000-0000-000000000044", "Name": "Dyes in the bags", "When": { "UiCategories": [ "Dye" ] }, "Then": { "Kind": 1 } },
              { "Id": "d4d4d4d4-0000-0000-0000-000000000444", "Name": "Dyes in the bags", "Enabled": false, "When": { "UiCategories": [ "Dye" ] }, "Then": { "Kind": 1 } }
            ]
          },
          {
            "$type": "Gleam.Core.Organizer.Model.OrganizerPlan, Gleam.Core",
            "Id": "b2b2b2b2-0000-0000-0000-000000000002",
            "Name": "Gear sorted",
            "Rules": [
              { "Id": "e5e5e5e5-0000-0000-0000-000000000005", "Name": "Set pieces", "When": { "Tags": [ 0 ], "InGearset": true }, "Then": { "Kind": 2 } },
              { "Id": "e5e5e5e5-0000-0000-0000-000000000055", "Name": "Materia to Mira", "When": { "Tags": [ 1 ] }, "Then": { "Kind": 4, "RetainerId": 99 } }
            ]
          },
          {
            "$type": "Gleam.Core.Organizer.Model.OrganizerPlan, Gleam.Core",
            "Id": "a1a1a1a1-0000-0000-0000-000000000001",
            "Name": "Housing only",
            "Rules": [
              { "Id": "a7a7a7a7-0000-0000-0000-000000000007", "Name": "Housing to Mira", "KeepInBags": 2, "When": { "Tags": [ 5 ] }, "Then": { "Kind": 4, "RetainerId": 99 } }
            ]
          }
        ]
      }
    }
    """;

    /// <summary>From 0.9: the simple screen's rules mixed into every layout, and a player who turned cleaning off.</summary>
    private const string Version13 = """
    {
      "$type": "Gleam.Configuration, Gleam",
      "Version": 13,
      "Profiles": {
        "$type": "Gleam.Core.Lists.ProfileStore, Gleam.Core",
        "Overrides": [ { "CharacterId": 7, "CharacterName": "An Alt", "OverriddenProperties": [ "LargeStackGuard" ], "Values": { "LargeStackGuard": 1 } } ]
      },
      "Automation": { "$type": "Gleam.AutomationSettings, Gleam", "Enabled": false },
      "Organizer": {
        "$type": "Gleam.OrganizerSettings, Gleam",
        "ActivePlanId": "a1a1a1a1-0000-0000-0000-000000000001",
        "Plans": [
          {
            "Id": "b2b2b2b2-0000-0000-0000-000000000002",
            "Name": "Simple",
            "Simple": true,
            "Rules": [
              { "Name": "Gear you are not using", "When": { "Tags": [ 0 ], "InGearset": false }, "Then": { "Kind": 4 } },
              { "Name": "Gear in a gear set", "When": { "Tags": [ 0 ], "InGearset": true }, "Then": { "Kind": 2 } },
              { "Name": "Housing items", "When": { "Tags": [ 5 ] }, "Then": { "Kind": 4 } }
            ]
          },
          {
            "Id": "a1a1a1a1-0000-0000-0000-000000000001",
            "Name": "Built by hand",
            "Rules": [
              { "Id": "c3c3c3c3-0000-0000-0000-000000000003", "Name": "Materia to the saddlebag", "When": { "Tags": [ 1 ] }, "Then": { "Kind": 3 } },
              { "Name": "Gear in a gear set", "When": { "Tags": [ 0 ], "InGearset": true }, "Then": { "Kind": 2 } },
              { "Name": "Housing items", "When": { "Tags": [ 5 ] }, "Then": { "Kind": 4 } },
              { "Id": "d4d4d4d4-0000-0000-0000-000000000004", "Name": "Minions to Mira", "When": { "Tags": [ 6 ] }, "Then": { "Kind": 4, "RetainerId": 99 } }
            ]
          }
        ]
      },
      "SeenFirstRun": true,
      "AdvancedMode": false,
      "UseClean": false,
      "UseOrganize": true,
      "AnsweredOrganizeOffer": true
    }
    """;

    /// <summary>From 0.9.5: a leftover per-character override, and hands-free left off.</summary>
    private const string Version16 = """
    {
      "$type": "Gleam.Configuration, Gleam",
      "Version": 16,
      "Profiles": {
        "$type": "Gleam.Core.Lists.ProfileStore, Gleam.Core",
        "Account": { "EnabledRules": [ "vendor-only-junk" ], "LargeStackGuard": 20 },
        "Overrides": [ { "CharacterId": 7, "CharacterName": "An Alt", "OverriddenProperties": [ "EnabledRules" ], "Values": { "EnabledRules": [] } } ]
      },
      "Automation": { "$type": "Gleam.AutomationSettings, Gleam", "Enabled": false, "UnseenRows": 0 },
      "Organizer": {
        "$type": "Gleam.OrganizerSettings, Gleam",
        "ActivePlanId": "a1a1a1a1-0000-0000-0000-000000000001",
        "Plans": [
          {
            "Id": "a1a1a1a1-0000-0000-0000-000000000001",
            "Name": "My layout",
            "Rules": [ { "Id": "c3c3c3c3-0000-0000-0000-000000000003", "Name": "Crystals to the saddlebag", "When": { "Tags": [ 4 ] }, "Then": { "Kind": 3 } } ]
          }
        ]
      },
      "SeenFirstRun": true
    }
    """;

    // ---------- version 2 ----------

    [Fact]
    public void A_version_2_file_reaches_the_current_version()
    {
        var s = Load(Version2);

        Assert.True(ConfigMigrations.Migrate(s));

        Assert.Equal(ConfigMigrations.CurrentVersion, s.Version);
    }

    [Fact]
    public void The_retired_cautious_preset_becomes_the_vendor_preset()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        Assert.Equal(PresetName.Vendor, s.Profiles.Account.Preset);
        Assert.Equal(Presets.For(PresetName.Vendor).Policy, s.Profiles.Account.Thresholds.Policy);
    }

    [Fact]
    public void Rules_the_player_turned_off_stay_off_and_only_the_two_new_rules_are_added()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        Assert.Equal(
            new[] { ObsoleteGearRule.RuleId, MarketPricePostProcessor.RuleId, RegisteredDuplicateRule.RuleId, VendorOnlyJunkRule.RuleId }.Order(),
            s.Profiles.Account.EnabledRules.Order());
    }

    [Fact]
    public void The_lists_and_the_rest_of_the_profile_survive_from_version_2()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        var kept = Assert.Single(s.ProtectList.Entries);
        Assert.Equal((5u, "my glamour"), (kept.ItemId, kept.Note));
        var always = Assert.Single(s.AlwaysDiscardList.Entries);
        Assert.Equal((7u, false), (always.ItemId, always.IncludeHq));
        Assert.Equal([42ul], s.Profiles.Account.ExcludedRetainerIds);
        Assert.Equal(50, s.Profiles.Account.LargeStackGuard);
        Assert.Equal(400, s.Callbacks.RateLimitMs);
    }

    [Fact]
    public void A_short_action_timeout_is_raised_to_eight_seconds()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        Assert.Equal(8000, s.Callbacks.ActionTimeoutMs);
    }

    [Fact]
    public void A_longer_timeout_the_player_chose_is_kept()
    {
        var s = new SavedSettings { Version = 2 };
        s.Callbacks.ActionTimeoutMs = 12000;

        ConfigMigrations.Migrate(s);

        Assert.Equal(12000, s.Callbacks.ActionTimeoutMs);
    }

    [Fact]
    public void Hands_free_steps_that_stopped_being_settings_are_switched_back_on()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        var a = s.Automation;
        Assert.True(a.Enabled && a.OpenSaddlebag && a.VisitRetainers && a.VisitDresser && a.SellAtVendor && a.VisitGrandCompany);
        Assert.False(a.VisitContainersWithoutRows);
        Assert.Equal(UnseenRowsMode.Clean, a.UnseenRows);
        Assert.True(s.UseUniversalis && s.UseAllaganTools);
        Assert.False(s.ActWhenMateriaFails);
    }

    [Fact]
    public void A_file_from_before_layouts_gets_exactly_one_starter_layout_in_use()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        var plan = Assert.Single(s.Organizer.Plans);
        Assert.Equal(plan.Id, s.Organizer.ActivePlanId);
        Assert.Equal(OrganizerPlan.Starter().Rules.Select(r => r.Name), plan.Rules.Select(r => r.Name));
    }

    [Fact]
    public void Someone_from_before_the_simple_screen_keeps_the_full_window_and_both_halves()
    {
        var s = Load(Version2);

        ConfigMigrations.Migrate(s);

        Assert.True(s.AdvancedMode);
        Assert.True(s.SeenFirstRun);
        Assert.True(s.UseClean && s.UseOrganize && s.AnsweredOrganizeOffer);
        Assert.Equal("0.9.5", s.LastSeenVersion);
        Assert.Empty(s.Profiles.Overrides);
    }

    [Fact]
    public void A_custom_preset_on_any_other_policy_is_left_as_it_was()
    {
        var s = new SavedSettings { Version = 3 };
        s.Profiles.Account.ApplyPreset(PresetName.Custom);
        s.Profiles.Account.Thresholds.Policy = ActionPolicy.DiscardAll;
        s.Profiles.Account.Thresholds.ObsoleteGearLevelGap = 30;

        ConfigMigrations.Migrate(s);

        Assert.Equal(PresetName.Custom, s.Profiles.Account.Preset);
        Assert.Equal(ActionPolicy.DiscardAll, s.Profiles.Account.Thresholds.Policy);
        Assert.Equal(30, s.Profiles.Account.Thresholds.ObsoleteGearLevelGap);
    }

    // ---------- version 7 ----------

    [Fact]
    public void The_retired_gear_rule_is_replaced_by_the_two_gear_set_rules_in_its_place()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        var mine = s.Organizer.Plans.Single(p => p.Id == MyLayout);
        Assert.Equal(
            ["Crystals to the saddlebag", "Gear set pieces in the armoury", "Other gear to a retainer", "Dyes in the bags", "Dyes in the bags"],
            mine.Rules.Select(r => r.Name));
        Assert.Equal(true, mine.Rules[1].When.InGearset);
        Assert.Equal(DestinationKind.Armoury, mine.Rules[1].Then.Kind);
        Assert.Equal(false, mine.Rules[2].When.InGearset);
        Assert.True(mine.Rules[2].Then.IsAnyRetainer);
    }

    [Fact]
    public void An_exact_copy_of_a_rule_goes_but_a_rule_that_differs_stays()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        var dyes = s.Organizer.Plans.Single(p => p.Id == MyLayout).Rules.Where(r => r.Name == "Dyes in the bags").ToList();
        Assert.Equal(2, dyes.Count);
        Assert.Equal(DyesRule, dyes[0].Id);
        Assert.True(dyes[0].Enabled);
        Assert.False(dyes[1].Enabled);
    }

    [Fact]
    public void The_players_own_rules_keep_their_ids_and_destinations()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        var crystals = s.Organizer.Plans.Single(p => p.Id == MyLayout).Rules[0];
        Assert.Equal(CrystalsRule, crystals.Id);
        Assert.Equal(DestinationKind.Saddlebag, crystals.Then.Kind);
        Assert.Equal([ItemTag.Crystals], crystals.When.Tags!);
    }

    [Fact]
    public void A_layout_already_on_gear_sets_is_not_touched()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        var gear = s.Organizer.Plans.Single(p => p.Id == GearLayout);
        Assert.Equal(["Set pieces", "Materia to Mira"], gear.Rules.Select(r => r.Name));
        Assert.Equal(SetPiecesRule, gear.Rules[0].Id);
        Assert.Equal(Destination.RetainerNamed(99), gear.Rules[1].Then);
        Assert.Equal(GearLayout, s.Organizer.ActivePlanId);
    }

    [Fact]
    public void A_layout_without_the_old_gear_rule_gets_the_gear_set_rules_at_the_end()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        var housing = s.Organizer.Plans.Single(p => p.Name == "Housing only");
        Assert.Equal(["Housing to Mira", "Gear set pieces in the armoury", "Other gear to a retainer"], housing.Rules.Select(r => r.Name));
        Assert.Equal(2, housing.Rules[0].KeepInBags);
        Assert.Equal(Destination.RetainerNamed(99), housing.Rules[0].Then);
    }

    [Fact]
    public void A_layout_saved_with_another_layouts_id_gets_one_of_its_own()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        Assert.Equal(3, s.Organizer.Plans.Count);
        Assert.Equal(3, s.Organizer.Plans.Select(p => p.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, s.Organizer.Plans.Select(p => p.Id));
        // The first one keeps the id; the active layout still points at the same one.
        Assert.Equal("My layout", s.Organizer.Plans.Single(p => p.Id == MyLayout).Name);
    }

    [Fact]
    public void A_file_that_already_has_layouts_gets_no_starter()
    {
        var s = Load(Version7);

        ConfigMigrations.Migrate(s);

        Assert.DoesNotContain(s.Organizer.Plans, p => p.Name == "Starter layout");
    }

    [Fact]
    public void Rules_that_differ_only_in_id_are_copies_and_any_other_difference_keeps_them()
    {
        var s = new SavedSettings { Version = 9 };
        var plan = new OrganizerPlan();
        OrganizerRule Rule(Destination to, int keep = 0) => new()
        {
            Name = "Materia", When = new OrganizerPredicate { Tags = [ItemTag.Materia] }, Then = to, KeepInBags = keep,
        };
        plan.Rules.AddRange([Rule(Destination.Saddlebag), Rule(Destination.Saddlebag), Rule(Destination.Bags), Rule(Destination.Saddlebag, keep: 5)]);
        s.Organizer.Plans.Add(plan);

        ConfigMigrations.Migrate(s);

        Assert.Equal(
            [(DestinationKind.Saddlebag, 0), (DestinationKind.Bags, 0), (DestinationKind.Saddlebag, 5)],
            plan.Rules.Select(r => (r.Then.Kind, r.KeepInBags)));
    }

    // ---------- version 13 ----------

    [Fact]
    public void The_simple_screens_rules_come_out_of_every_layout_and_the_players_own_stay()
    {
        var s = Load(Version13);

        ConfigMigrations.Migrate(s);

        var built = s.Organizer.Plans.Single(p => p.Id == MyLayout);
        Assert.Equal(["Materia to the saddlebag", "Minions to Mira"], built.Rules.Select(r => r.Name));
        Assert.Equal([CrystalsRule, DyesRule], built.Rules.Select(r => r.Id));
        Assert.Equal(Destination.RetainerNamed(99), built.Rules[1].Then);
    }

    [Fact]
    public void No_layout_stays_marked_as_the_simple_screens_own()
    {
        var s = Load(Version13);

        ConfigMigrations.Migrate(s);

        Assert.All(s.Organizer.Plans, p => Assert.False(p.Simple));
        // The one the simple screen was editing is kept, empty, rather than deleted with the player's layouts.
        Assert.Empty(s.Organizer.Plans.Single(p => p.Id == GearLayout).Rules);
        Assert.Equal(MyLayout, s.Organizer.ActivePlanId);
    }

    [Fact]
    public void Choices_made_on_the_first_screen_after_version_13_are_kept()
    {
        var s = Load(Version13);

        ConfigMigrations.Migrate(s);

        Assert.False(s.UseClean);
        Assert.True(s.UseOrganize);
        Assert.False(s.AdvancedMode);
    }

    [Fact]
    public void Travelling_is_switched_on_for_a_file_from_before_version_15()
    {
        var s = Load(Version13);

        ConfigMigrations.Migrate(s);

        Assert.True(s.Automation.Enabled);
    }

    [Fact]
    public void Rules_given_ids_on_load_have_ids_after_migrating()
    {
        var s = Load(Version13);

        ConfigMigrations.Migrate(s);

        Assert.All(s.Organizer.Plans.SelectMany(p => p.Rules), r => Assert.NotEqual(Guid.Empty, r.Id));
    }

    // ---------- version 16 ----------

    [Fact]
    public void Leftover_per_character_overrides_are_cleared_and_the_account_profile_is_untouched()
    {
        // The same file one version earlier: clearing overrides is the step that made it 16.
        var s = Load(Version16.Replace("\"Version\": 16", "\"Version\": 15"));

        ConfigMigrations.Migrate(s);

        Assert.Empty(s.Profiles.Overrides);
        Assert.Equal([VendorOnlyJunkRule.RuleId], s.Profiles.Account.EnabledRules);
        Assert.Equal(20, s.Profiles.Account.LargeStackGuard);
    }

    [Fact]
    public void Only_steps_newer_than_the_file_run()
    {
        var s = Load(Version16);

        ConfigMigrations.Migrate(s);

        // Version 15 switched travelling on once; a player on 16 with it off is not overruled again. Overrides were
        // cleared by step 16 itself, so one in a file already at 16 is left where it is.
        Assert.False(s.Automation.Enabled);
        Assert.Single(s.Profiles.Overrides);
        Assert.Equal(UnseenRowsMode.Skip, s.Automation.UnseenRows);
        var plan = Assert.Single(s.Organizer.Plans);
        Assert.Equal([CrystalsRule], plan.Rules.Select(r => r.Id));
    }

    [Fact]
    public void Someone_past_the_first_screen_is_shown_what_came_after_0_9_5()
    {
        var s = Load(Version16);

        ConfigMigrations.Migrate(s);

        Assert.Equal("0.9.5", s.LastSeenVersion);
    }

    [Fact]
    public void A_release_already_marked_as_seen_is_not_moved_back()
    {
        var s = new SavedSettings { Version = 16, SeenFirstRun = true, LastSeenVersion = "0.9.3" };

        ConfigMigrations.Migrate(s);

        Assert.Equal("0.9.3", s.LastSeenVersion);
    }

    [Fact]
    public void Someone_who_never_saw_the_first_screen_is_not_shown_old_release_notes()
    {
        var s = new SavedSettings { Version = 16, SeenFirstRun = false };

        ConfigMigrations.Migrate(s);

        Assert.Null(s.LastSeenVersion);
    }

    // ---------- the current version, and every version ----------

    [Fact]
    public void A_current_config_is_left_alone()
    {
        var s = new SavedSettings();
        s.Organizer.Plans.Add(OrganizerPlan.Starter());
        var before = Save(s);

        Assert.False(ConfigMigrations.Migrate(s));

        Assert.Equal(before, Save(s));
    }

    [Theory]
    [InlineData(Version2)]
    [InlineData(Version7)]
    [InlineData(Version13)]
    [InlineData(Version16)]
    public void Migrating_twice_changes_nothing_the_second_time(string file)
    {
        var s = Load(file);
        ConfigMigrations.Migrate(s);
        var once = Save(s);

        Assert.False(ConfigMigrations.Migrate(s));

        Assert.Equal(once, Save(s));
    }

    [Theory]
    [InlineData(Version2)]
    [InlineData(Version7)]
    [InlineData(Version13)]
    [InlineData(Version16)]
    public void A_migrated_file_survives_a_restart_without_doubling_anything(string file)
    {
        var s = Load(file);
        ConfigMigrations.Migrate(s);

        var loaded = Load(Save(s));

        Assert.False(ConfigMigrations.Migrate(loaded));
        Assert.Equal(s.Organizer.Plans.Select(p => (p.Id, p.Rules.Count)), loaded.Organizer.Plans.Select(p => (p.Id, p.Rules.Count)));
        Assert.Equal(s.Profiles.Account.EnabledRules.Order(), loaded.Profiles.Account.EnabledRules.Order());
        Assert.Equal(s.ProtectList.Entries.Count, loaded.ProtectList.Entries.Count);
    }

    [Fact]
    public void What_the_migration_steps_see_is_never_written_to_the_file()
    {
        var json = Save(new SavedSettings());

        Assert.Contains("\"$type\": \"Gleam.Configuration, Gleam\"", json);
        Assert.DoesNotContain("IMigratable", json);
        // The timeout is written once, inside the callback settings, not a second time at the top.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(json, "\"ActionTimeoutMs\""));
    }

    [Fact]
    public void A_config_with_no_layout_gets_the_starter_in_use()
    {
        var s = new SavedSettings();

        Assert.True(ConfigMigrations.EnsureDefaults(s));

        var plan = Assert.Single(s.Organizer.Plans);
        Assert.Equal("Starter layout", plan.Name);
        Assert.Equal(plan.Id, s.Organizer.ActivePlanId);
    }

    [Fact]
    public void Empty_or_repeated_ids_are_replaced_and_good_ones_kept()
    {
        var s = new SavedSettings();
        var first = new OrganizerPlan { Id = MyLayout };
        var sameId = new OrganizerPlan { Id = MyLayout };
        var noId = new OrganizerPlan { Id = Guid.Empty };
        first.Rules.AddRange([new OrganizerRule { Id = CrystalsRule }, new OrganizerRule { Id = CrystalsRule }, new OrganizerRule { Id = Guid.Empty }]);
        // The same rule id in another layout is fine: ids only need to be unique within a layout.
        sameId.Rules.Add(new OrganizerRule { Id = CrystalsRule });
        s.Organizer.Plans.AddRange([first, sameId, noId]);

        Assert.True(ConfigMigrations.EnsureDefaults(s));

        Assert.Equal(MyLayout, first.Id);
        Assert.NotEqual(MyLayout, sameId.Id);
        Assert.NotEqual(Guid.Empty, noId.Id);
        Assert.Equal(CrystalsRule, first.Rules[0].Id);
        Assert.Equal(3, first.Rules.Select(r => r.Id).Distinct().Count());
        Assert.DoesNotContain(Guid.Empty, first.Rules.Select(r => r.Id));
        Assert.Equal(CrystalsRule, sameId.Rules[0].Id);
    }

    [Fact]
    public void Ids_that_are_already_fine_report_no_change()
    {
        var s = new SavedSettings();
        s.Organizer.Plans.Add(OrganizerPlan.Starter());

        Assert.False(ConfigMigrations.EnsureDefaults(s));
    }
}
