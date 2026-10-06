using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;

namespace NOOSE_Website.Services;

/// <summary>One partner-visible tab of a record type (slug matches the detail page's tab key).</summary>
public sealed record PartnerTab(string Slug, string Label);

/// <summary>A releasable record type with its route prefix and partner-visible tabs.</summary>
public sealed record PartnerRecordType(string TypeKey, string RoutePrefix, string DisplayName, IReadOnlyList<PartnerTab> Tabs);

/// <summary>Catalog of releasable record types and their partner-visible tabs — single source for the admin UI and tab gating.</summary>
public static class PartnerTabCatalog
{
    /// <summary>All releasable types in nav order; tab slugs mirror each *Detail page (internal-only "agents" tab excluded).</summary>
    public static readonly IReadOnlyList<PartnerRecordType> All = new[]
    {
        new PartnerRecordType(nameof(Person), "personen", "Personen-Akten", new PartnerTab[]
        {
            new("steckbrief", "Steckbrief"), new("doks", "Doks"), new("ueberwachung", "Überwachung"),
            new("einstufung", "Einstufung"), new("gefaehrdung", "Gefährdung"), new("fotos", "Fotos"),
            new("quellen", "Quellen"), new("wiedervorlagen", "Wiedervorlagen"), new("verknuepfungen", "Verknüpfungen"),
            new("zugehoerigkeit", "Zugehörigkeit"), new("kommentare", "Kommentare"), new("zusatzfelder", "Zusatzfelder"),
            new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Faction), "fraktionen", "Fraktionen", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("mitglieder", "Mitglieder"), new("bestaende", "Bestände"),
            new("aktivitaeten", "Aktivitäten"), new("fotos", "Fotos"), new("einstufung", "Einstufung"),
            new("gefaehrdung", "Gefährdung"), new("doks", "Doks"), new("beziehungen", "Beziehungen"),
            new("quellen", "Quellen"), new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"),
            new("zusatzfelder", "Zusatzfelder"), new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(PersonGroup), "personengruppen", "Personengruppen", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("mitglieder", "Mitglieder"), new("aktivitaeten", "Aktivitäten"),
            new("fotos", "Fotos"), new("einstufung", "Einstufung"),
            new("doks", "Doks"), new("beziehungen", "Beziehungen"), new("quellen", "Quellen"),
            new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"), new("zusatzfelder", "Zusatzfelder"),
            new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Party), "parteien", "Parteien", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("mitglieder", "Mitglieder"), new("fotos", "Fotos"),
            new("einstufung", "Einstufung"),
            new("doks", "Doks"), new("beziehungen", "Beziehungen"), new("quellen", "Quellen"),
            new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"), new("zusatzfelder", "Zusatzfelder"),
            new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Operation), "operationen", "Operationen", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("beziehungen", "Beteiligte / Beziehungen"), new("einstufung", "Einstufung"),
            new("quellen", "Quellen"), new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"),
            new("zusatzfelder", "Zusatzfelder"), new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Case), "vorgaenge", "Vorgänge", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("inhalt", "Inhalt"), new("einstufung", "Einstufung"),
            new("quellen", "Quellen"), new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"),
            new("zusatzfelder", "Zusatzfelder"), new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Taskforce), "taskforces", "Taskforces", new PartnerTab[]
        {
            new("stammdaten", "Stammdaten"), new("chat", "Chat"), new("beziehungen", "Beziehungen"),
            new("quellen", "Quellen"), new("wiedervorlagen", "Wiedervorlagen"), new("kommentare", "Kommentare"),
            new("zusatzfelder", "Zusatzfelder"), new("historie", "Zeitstrahl"),
        }),
        new PartnerRecordType(nameof(Document), "dokumente", "Dokumente", Array.Empty<PartnerTab>()),
        new PartnerRecordType(nameof(Law), "gesetze", "Gesetzbuch", Array.Empty<PartnerTab>()),
    };

    private static readonly Dictionary<string, PartnerRecordType> ByPrefix =
        All.ToDictionary(t => t.RoutePrefix, StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, PartnerRecordType> ByTypeKey =
        All.ToDictionary(t => t.TypeKey);

    /// <summary>Type key for a route prefix (e.g. "fraktionen" → "Faction"), or null.</summary>
    public static string? TypeKeyForPrefix(string prefix)
        => ByPrefix.GetValueOrDefault(prefix)?.TypeKey;

    /// <summary>Type key for a relative path's first segment, or null if it is not a releasable type.</summary>
    public static string? TypeKeyForPath(string? relativePath)
    {
        var path = (relativePath ?? string.Empty).Split('?')[0].Split('#')[0].Trim('/').ToLowerInvariant();
        if (path.Length == 0)
        {
            return null;
        }
        return ByPrefix.GetValueOrDefault(path.Split('/')[0])?.TypeKey;
    }

    /// <summary>True for paths that self-guard partner visibility per record (documents: universal authoring + own-authored), so the coarse type gate must not apply.</summary>
    public static bool IsTypeGateExempt(string? relativePath)
        => TypeKeyForPath(relativePath) == nameof(Document);

    /// <summary>Route prefix for a type key, or null.</summary>
    public static string? PrefixForTypeKey(string typeKey)
        => ByTypeKey.GetValueOrDefault(typeKey)?.RoutePrefix;

    /// <summary>All partner-visible tab slugs of a type; empty for unknown or tab-less types.</summary>
    public static IEnumerable<string> TabSlugs(string typeKey)
        => ByTypeKey.GetValueOrDefault(typeKey)?.Tabs.Select(t => t.Slug) ?? Enumerable.Empty<string>();
}
