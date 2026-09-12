using Gleam.Core.Model;
using Gleam.Core.Organizer.Model;
using Gleam.Core.Rules;

namespace Gleam.Core.Settings;

/// <summary>
/// Brings settings written by an older build up to date, one numbered step at a time. These steps rewrite what
/// players have saved, rules and layouts included, so they live here where tests can run them against old files.
/// </summary>
public static class ConfigMigrations
{
    /// <summary>Bump when <see cref="Migrate"/> gains a step. New configs start here and skip the chain.</summary>
    public const int CurrentVersion = 18;

    /// <summary>Brings a config written by an older build up to current defaults where the old default was a mistake.</summary>
    /// <returns>Whether anything changed, so the caller knows to save.</returns>
    public static bool Migrate(IMigratableSettings s)
    {
        var changed = false;
        var automation = s.Automation;
        var organizer = s.Organizer;
        if (s.Version < 3)
        {
            if (s.ActionTimeoutMs < 8000) s.ActionTimeoutMs = 8000;
            s.Version = 3;
            changed = true;
        }
        if (s.Version < 4)
        {
            // The Cautious preset is gone; anyone on it lands on Balanced.
            if (s.Profiles.Account.Thresholds.Policy == ActionPolicy.SellOnly) s.Profiles.Account.ApplyPreset(PresetName.Vendor);
            foreach (var o in s.Profiles.Overrides)
                if (o.Values.Thresholds.Policy == ActionPolicy.SellOnly) o.Values.ApplyPreset(PresetName.Vendor);
            s.Version = 4;
            changed = true;
        }
        if (s.Version < 5)
        {
            // These stopped being settings; make sure nobody is stuck with an old "off".
            s.UseUniversalis = true;
            s.UseAllaganTools = true;
            s.ActWhenMateriaFails = false;
            automation.OpenSaddlebag = automation.VisitRetainers = automation.VisitDresser = automation.SellAtVendor = automation.VisitGrandCompany = true;
            automation.VisitContainersWithoutRows = false;
            s.Profiles.Account.EnabledRules.Add(MarketPricePostProcessor.RuleId);
            s.Version = 5;
            changed = true;
        }
        if (s.Version < 6)
        {
            s.Profiles.Account.EnabledRules.Add(RegisteredDuplicateRule.RuleId);
            foreach (var o in s.Profiles.Overrides) o.Values.EnabledRules.Add(RegisteredDuplicateRule.RuleId);
            s.Version = 6;
            changed = true;
        }
        if (s.Version < 7)
        {
            if (!automation.Enabled) { automation.Enabled = true; changed = true; }
            if (organizer.Plans.Count == 0)
            {
                var starter = OrganizerPlan.Starter();
                organizer.Plans.Add(starter);
                organizer.ActivePlanId = starter.Id;
            }
            s.Version = 7;
            changed = true;
        }
        if (s.Version < 8)
        {
            foreach (var plan in organizer.Plans)
            {
                if (plan.Rules.Any(r => r.When.InGearset is not null)) continue;
                var old = plan.Rules.FindIndex(r => r.Name == "Gear I can wear in the armoury");
                if (old >= 0) plan.Rules.RemoveAt(old);
                var at = old >= 0 ? old : plan.Rules.Count;
                plan.Rules.Insert(at, new OrganizerRule
                {
                    Name = "Gear set pieces in the armoury",
                    When = new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = true },
                    Then = Destination.Armoury,
                });
                plan.Rules.Insert(at + 1, new OrganizerRule
                {
                    Name = "Other gear to a retainer",
                    When = new OrganizerPredicate { Tags = [ItemTag.Gear], InGearset = false },
                    Then = Destination.AnyRetainer,
                });
            }
            s.Version = 8;
            changed = true;
        }
        if (s.Version < 9)
        {
            // The "ask at every container" mode is gone; anyone on it gets the hands-free default.
            if (automation.UnseenRows is not (UnseenRowsMode.Skip or UnseenRowsMode.Clean)) automation.UnseenRows = UnseenRowsMode.Clean;
            s.Version = 9;
            changed = true;
        }
        if (s.Version < 10)
        {
            // Destinations used to share one default object that the serializer filled in place, so every rule
            // came back as "stays where it is" and players re-made their rules. Drop the exact copies that left.
            RemoveExactDuplicateRules(organizer);
            s.Version = 10;
            changed = true;
        }
        if (s.Version < 11)
        {
            // The computed "active layout" was being serialized and filled in place on load, doubling every rule
            // list on each start. Fixed at the source; clean up the copies once more.
            RemoveExactDuplicateRules(organizer);
            s.Version = 11;
            changed = true;
        }
        if (s.Version < 12)
        {
            // Anyone who has been using the full window keeps it; the simple layer is for newcomers.
            s.AdvancedMode = true;
            s.SeenFirstRun = true;
            s.Version = 12;
            changed = true;
        }
        if (s.Version < 13)
        {
            // Anyone already here has both halves in front of them; keep it that way.
            s.UseClean = true;
            s.UseOrganize = true;
            s.AnsweredOrganizeOffer = true;
            s.Version = 13;
            changed = true;
        }
        if (s.Version < 14)
        {
            // The simple screen used to edit whichever layout was active, which rewrote layouts built by hand.
            // It owns one of its own now; take the rules it added back out of everyone else's.
            string[] itsOwn = ["Gear you are not using", "Gear in a gear set", "Housing items"];
            foreach (var plan in organizer.Plans)
            {
                plan.Simple = false;
                plan.Rules.RemoveAll(r => itsOwn.Contains(r.Name));
            }
            s.Version = 14;
            changed = true;
        }
        if (s.Version < 15)
        {
            // Travelling is how Gleam works now, not a mode you switch on.
            automation.Enabled = true;
            s.Version = 15;
            changed = true;
        }
        if (s.Version < 16)
        {
            // Per-character overrides lost their settings page before the first public release, but the
            // effective profile still honoured them: a leftover one would quietly change what Gleam does.
            s.Profiles.Overrides.Clear();
            s.Version = 16;
            changed = true;
        }
        if (s.Version < 17)
        {
            // Everyone here was on 0.9.5 or earlier, before the "what's new" card: show them what came since.
            if (s.SeenFirstRun && s.LastSeenVersion is null) s.LastSeenVersion = "0.9.5";
            s.Version = 17;
            changed = true;
        }
        if (s.Version < 18)
        {
            // "Discard all" used to widen what counts as junk as well. It now only decides what happens to junk, and a
            // profile still on the old values moves to the new ones rather than turning into an unnamed custom mix.
            if (Presets.IsLegacyDiscardAll(s.Profiles.Account.Thresholds)) s.Profiles.Account.ApplyPreset(PresetName.DiscardAll);
            s.Version = 18;
            changed = true;
        }
        changed |= EnsureDefaults(s);
        return changed;
    }

    private static void RemoveExactDuplicateRules(IMigratableLayouts organizer)
    {
        foreach (var plan in organizer.Plans)
        {
            var seen = new HashSet<string>();
            plan.Rules.RemoveAll(r => !seen.Add(System.Text.Json.JsonSerializer.Serialize(new { r.Name, r.Enabled, r.Then, r.KeepInBags, When = System.Text.Json.JsonSerializer.Serialize(r.When) })));
        }
    }

    /// <summary>What every config needs regardless of age: a first layout to organize with.</summary>
    public static bool EnsureDefaults(IMigratableSettings s)
    {
        var changed = false;
        var organizer = s.Organizer;
        if (organizer.Plans.Count == 0)
        {
            var starter = OrganizerPlan.Starter();
            organizer.Plans.Add(starter);
            organizer.ActivePlanId = starter.Id;
            changed = true;
        }

        // Ids are what the UI and the solver key on; an older save can carry empty or repeated ones.
        var planIds = new HashSet<Guid>();
        foreach (var plan in organizer.Plans)
        {
            if (plan.Id == Guid.Empty || !planIds.Add(plan.Id)) { plan.Id = Guid.NewGuid(); planIds.Add(plan.Id); changed = true; }
            var ruleIds = new HashSet<Guid>();
            foreach (var rule in plan.Rules)
                if (rule.Id == Guid.Empty || !ruleIds.Add(rule.Id)) { rule.Id = Guid.NewGuid(); ruleIds.Add(rule.Id); changed = true; }
        }
        return changed;
    }
}
