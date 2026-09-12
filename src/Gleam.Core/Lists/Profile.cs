using System.Reflection;
using System.Runtime.Serialization;
using Gleam.Core.Model;
using Gleam.Core.Rules;

namespace Gleam.Core.Lists;

/// <summary>Everything a run reads that a user might want per character. Mutable for config round-tripping.</summary>
public sealed class Profile
{
    public PresetName Preset { get; set; } = PresetName.Vendor;
    public Thresholds Thresholds { get; set; } = Presets.For(PresetName.Vendor);

    public HashSet<string> EnabledRules { get; set; } = new(RuleEngine.AllRuleIds);

    /// <summary>
    /// Dalamud's loader fills an existing collection instead of replacing it. EnabledRules starts full, so
    /// loading added the saved rules to all of them and a rule the player turned off came back on every
    /// restart. Emptying it first makes the saved set the whole truth.
    /// </summary>
    [OnDeserializing]
    private void BeforeLoad(StreamingContext _) => EnabledRules = new HashSet<string>();

    public Dictionary<ContainerKind, bool> ContainerEnabled { get; set; } =
        Enum.GetValues<ContainerKind>().ToDictionary(k => k, _ => true);

    public Dictionary<ContainerKind, bool> AutoOpenOnContainer { get; set; } =
        Enum.GetValues<ContainerKind>().ToDictionary(k => k, _ => false);

    public HashSet<ulong> ExcludedRetainerIds { get; set; } = new();

    public bool PostDutyNudge { get; set; } = true;
    public bool StackMergeBeforeScan { get; set; } = true;
    public bool ShowDtrEntry { get; set; } = true;
    public int FullnessNudgePercent { get; set; } = 90;

    /// <summary>Rules leave stacks at least this big alone; the player can still tick them by hand. 0 turns the guard off.</summary>
    public int LargeStackGuard { get; set; } = 200;

    public Profile Clone()
    {
        var c = (Profile)MemberwiseClone();
        c.Thresholds = Thresholds.Clone();
        c.EnabledRules = new HashSet<string>(EnabledRules);
        c.ContainerEnabled = new Dictionary<ContainerKind, bool>(ContainerEnabled);
        c.AutoOpenOnContainer = new Dictionary<ContainerKind, bool>(AutoOpenOnContainer);
        c.ExcludedRetainerIds = new HashSet<ulong>(ExcludedRetainerIds);
        return c;
    }

    public void ApplyPreset(PresetName preset)
    {
        Preset = preset;
        if (preset != PresetName.Custom) Thresholds = Presets.For(preset);
    }

    /// <summary>Whether Gleam may open this place. Bags always: everything it moves or sells passes through them.</summary>
    public bool IsContainerEnabled(ContainerKind kind) => kind == ContainerKind.Inventory || !ContainerEnabled.TryGetValue(kind, out var v) || v;
    public bool IsAutoOpen(ContainerKind kind) => AutoOpenOnContainer.TryGetValue(kind, out var v) && v;

    /// <summary>Names of the properties a per-character override may replace.</summary>
    public static IReadOnlyList<string> OverridableProperties { get; } =
        typeof(Profile).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite).Select(p => p.Name).ToList();
}

/// <summary>A character's deviations from the account profile: only the named properties are taken from <see cref="Values"/>.</summary>
public sealed class CharacterOverride
{
    public ulong CharacterId { get; set; }
    public string CharacterName { get; set; } = string.Empty;
    public HashSet<string> OverriddenProperties { get; set; } = new();
    public Profile Values { get; set; } = new();
}

/// <summary>Account-wide profile plus per-character overrides, resolved to an effective profile.</summary>
public sealed class ProfileStore
{
    public Profile Account { get; set; } = new();
    public List<CharacterOverride> Overrides { get; set; } = new();

    public CharacterOverride GetOrCreateOverride(ulong characterId, string characterName)
    {
        var o = Overrides.FirstOrDefault(x => x.CharacterId == characterId);
        if (o is null)
        {
            o = new CharacterOverride { CharacterId = characterId, CharacterName = characterName, Values = Account.Clone() };
            Overrides.Add(o);
        }
        else if (!string.IsNullOrEmpty(characterName))
        {
            o.CharacterName = characterName;
        }
        return o;
    }

    public bool IsOverridden(ulong characterId, string property) =>
        Overrides.FirstOrDefault(x => x.CharacterId == characterId)?.OverriddenProperties.Contains(property) == true;

    public void SetOverridden(ulong characterId, string characterName, string property, bool overridden)
    {
        var o = GetOrCreateOverride(characterId, characterName);
        if (overridden)
        {
            o.OverriddenProperties.Add(property);
            // Start the override from the current effective value so toggling it on changes nothing yet.
            CopyProperty(Account, o.Values, property);
        }
        else
        {
            o.OverriddenProperties.Remove(property);
        }
    }

    /// <summary>The profile a run should actually use for this character.</summary>
    public Profile Effective(ulong characterId)
    {
        var result = Account.Clone();
        var o = Overrides.FirstOrDefault(x => x.CharacterId == characterId);
        if (o is null) return result;
        foreach (var prop in o.OverriddenProperties)
            CopyProperty(o.Values, result, prop);
        return result;
    }

    private static void CopyProperty(Profile from, Profile to, string property)
    {
        var p = typeof(Profile).GetProperty(property, BindingFlags.Public | BindingFlags.Instance);
        if (p is null || !p.CanWrite) return;
        var value = p.GetValue(from);
        // Deep-copy the mutable members so an override never aliases the account profile.
        value = value switch
        {
            Thresholds t => t.Clone(),
            HashSet<string> hs => new HashSet<string>(hs),
            HashSet<ulong> hu => new HashSet<ulong>(hu),
            Dictionary<ContainerKind, bool> d => new Dictionary<ContainerKind, bool>(d),
            _ => value,
        };
        p.SetValue(to, value);
    }
}
