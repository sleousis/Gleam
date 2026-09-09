using System.Text;
using System.Text.Json;

namespace TidyUp.Core.Organizer.Model;

/// <summary>A layout as one line of text, so players can share them. Retainers are per account, so named ones the importer does not own become "any retainer".</summary>
public static class OrganizerPlanCodec
{
    private const string Prefix = "TIDYUP1:";

    public static string Export(OrganizerPlan plan) =>
        Prefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(plan)));

    public static OrganizerPlan? TryImport(string? text, IReadOnlyCollection<ulong> knownRetainers)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        OrganizerPlan? plan;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(text[Prefix.Length..]));
            plan = JsonSerializer.Deserialize<OrganizerPlan>(json);
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
        if (plan is null) return null;

        var fresh = plan.Clone();
        if (string.IsNullOrWhiteSpace(fresh.Name)) fresh.Name = "Imported layout";
        foreach (var r in fresh.Rules) r.Then = Localize(r.Then, knownRetainers);
        fresh.Fallback = Localize(fresh.Fallback, knownRetainers);
        fresh.RetainersInScope.RemoveWhere(id => !knownRetainers.Contains(id));
        return fresh;
    }

    private static Destination Localize(Destination d, IReadOnlyCollection<ulong> known) =>
        d.Kind == DestinationKind.Retainer && d.RetainerId != 0 && !known.Contains(d.RetainerId) ? Destination.AnyRetainer : d;
}
