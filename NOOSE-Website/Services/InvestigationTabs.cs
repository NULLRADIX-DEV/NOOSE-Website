using System.Text.Json;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>One tab of the investigation file.</summary>
public sealed record InvestigationTab(string Slug, string Label, string Icon, bool ReadOnly = false);

/// <summary>The tabs of the investigation file: the only source for slugs, labels and which child type lives where.</summary>
public static class InvestigationTabs
{
    public const string Overview = "uebersicht";
    public const string Charges = "tatvorwurf";
    public const string Narrative = "tathergang";
    public const string People = "personen";
    public const string Evidence = "beweismittel";
    public const string Interrogations = "vernehmungen";
    public const string Tasks = "aufgaben";
    public const string Notes = "notizen";
    public const string Warrants = "anordnungen";
    public const string Participants = "beteiligte";
    public const string History = "verlauf";

    public static readonly IReadOnlyList<InvestigationTab> All = new[]
    {
        new InvestigationTab(Overview, "Übersicht", MudBlazor.Icons.Material.Outlined.Summarize),
        new InvestigationTab(Charges, "Tatvorwurf", MudBlazor.Icons.Material.Outlined.Gavel),
        new InvestigationTab(Narrative, "Tathergang", MudBlazor.Icons.Material.Outlined.Article),
        new InvestigationTab(People, "Zeugen & Beschuldigte", MudBlazor.Icons.Material.Outlined.Groups),
        new InvestigationTab(Evidence, "Beweismittel", MudBlazor.Icons.Material.Outlined.Inventory2),
        new InvestigationTab(Interrogations, "Vernehmungen", MudBlazor.Icons.Material.Outlined.RecordVoiceOver),
        new InvestigationTab(Tasks, "To-Do", MudBlazor.Icons.Material.Outlined.Checklist),
        new InvestigationTab(Notes, "Notizen", MudBlazor.Icons.Material.Outlined.StickyNote2),
        new InvestigationTab(Warrants, "Anordnungen", MudBlazor.Icons.Material.Outlined.Policy),
        new InvestigationTab(Participants, "Beteiligte & Zugriff", MudBlazor.Icons.Material.Outlined.AdminPanelSettings),
        new InvestigationTab(History, "Verlauf", MudBlazor.Icons.Material.Outlined.History, ReadOnly: true),
    };

    /// <summary>All slugs in display order.</summary>
    public static readonly IReadOnlyList<string> Slugs = All.Select(t => t.Slug).ToArray();

    /// <summary>Tab metadata for a slug, or null.</summary>
    public static InvestigationTab? Find(string slug) => All.FirstOrDefault(t => t.Slug == slug);

    /// <summary>Label for a slug.</summary>
    public static string Label(string slug) => Find(slug)?.Label ?? slug;

    /// <summary>The tab a child row lives on; null for the file itself.</summary>
    public static string? ForChildType(string childType) => childType switch
    {
        "InvestigationCharge" => Charges,
        "InvestigationPerson" => People,
        "InvestigationEvidence" => Evidence,
        "InvestigationInterrogation" or "InvestigationInterrogationEntry" => Interrogations,
        "InvestigationTask" => Tasks,
        "InvestigationNote" => Notes,
        "InvestigationParticipant" or "InvestigationItemRestriction" => Participants,
        "Warrant" => Warrants,
        _ => null,
    };

    /// <summary>Highest level the tab allows (history is read-only).</summary>
    public static InvestigationAccessLevel Cap(string slug, InvestigationAccessLevel level)
        => Find(slug)?.ReadOnly == true && level > InvestigationAccessLevel.Read ? InvestigationAccessLevel.Read : level;
}

/// <summary>Per-tab access maps stored as JSON (unit defaults, participant overrides).</summary>
public static class InvestigationTabAccess
{
    /// <summary>Known slugs only; anything unreadable counts as absent.</summary>
    public static IReadOnlyDictionary<string, InvestigationAccessLevel> Parse(string? raw)
    {
        var result = new Dictionary<string, InvestigationAccessLevel>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return result;
        }
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, int>>(raw) ?? new();
            foreach (var (slug, value) in parsed)
            {
                if (InvestigationTabs.Find(slug) is not null && Enum.IsDefined(typeof(InvestigationAccessLevel), value))
                {
                    result[slug] = InvestigationTabs.Cap(slug, (InvestigationAccessLevel)value);
                }
            }
        }
        catch (JsonException)
        {
            /* ignore */
        }
        return result;
    }

    /// <summary>Null for an empty map, so "no setting" stays visibly empty in the row.</summary>
    public static string? Serialize(IReadOnlyDictionary<string, InvestigationAccessLevel>? map)
    {
        if (map is null)
        {
            return null;
        }
        var clean = map.Where(kv => InvestigationTabs.Find(kv.Key) is not null)
            .ToDictionary(kv => kv.Key, kv => (int)InvestigationTabs.Cap(kv.Key, kv.Value));
        return clean.Count == 0 ? null : JsonSerializer.Serialize(clean);
    }
}
