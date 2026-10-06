namespace NOOSE_Website.Navigation;

/// <summary>Section slugs of the pages that absorbed others in V1.5; keeps LegacyRoutes and the pages in sync.</summary>
public static class MergedPageSections
{
    public static readonly string[] Settings =
    [
        "system", "discord", "status", "neuerungen",
        "custom-felder", "tags", "aktualitaet", "bedrohungs-score",
        "vorlagen-dok", "vorlagen-dokument", "vorlagen-aktivitaet", "vorlagen-personal",
        "module", "einladungen",
        "finanzierung",
        "noosei", "ki-anbieter", "ki-regeln", "ki-betrieb", "ki-kontingente", "ki-anfragen",
        "partner",
        "oeffentliche-module", "oeffentliche-seiten", "oeffentliche-faq", "presse", "lage", "warnungen", "berichte", "recht",
        "oeffentliche-kennzahlen",
        "oeffentliche-fuehrung",
        "warnhinweise",
        "oeffentliche-vorlagen", "buerger",
        "basisdaten",
    ];

    public static readonly string[] Monitoring =
    [
        "chronik",
        "aenderungen", "zugriffe",
        "lagebild", "agenten-profil", "auffaelligkeiten", "regeln",
    ];

    public static readonly string[] Trash =
    [
        "personen", "fraktionen", "personengruppen", "parteien",
        "vorgaenge", "operationen", "taskforces", "aufgaben",
        "brett", "kalender", "besprechungen", "aktivitaeten", "abmeldungen", "entfuehrungen",
        "informanten", "funk",
        "asservate-items", "asservate-eintraege", "kasse-buchungen", "finanzierungen",
        "feedback", "oeffentliche-seiten", "oeffentliche-fahndungen", "oeffentliche-fraktionsprofile",
        "hinweise", "tickets", "fahndungs-einsprueche", "pressemitteilungen", "oeffentliche-warnungen", "oeffentliche-lageberichte",
        "neuerungen", "handbuch-kapitel", "handbuch-artikel", "gesetzbuecher", "gesetze",
    ];

    public static readonly string[] Wanted =
        ["fahndung", "oeffentlich", "organisationen", "einsprueche", "observationen", "doks"];

    public static readonly string[] Absences = ["meine", "uebersicht", "papierkorb"];

    public static readonly string[] Recruiting = ["eingang", "sperren", "vorlagen", "tests", "anforderungen", "automatik"];

    public static readonly string[] Statistics =
    [
        "ueberblick", "bestand", "aktivitaet", "bedrohung", "entfuehrungen", "netzwerk", "kasse", "finanzierung", "dienststelle", "lageberichte",
    ];

    public static readonly string[] Financing =
    [
        "meine", "alle", "katalog", "budgets", "statistik", "papierkorb",
    ];

    /// <summary>Section slugs of a merged page, keyed by its route.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> ByRoute =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/einstellungen"] = Settings,
            ["/papierkorb"] = Trash,
            ["/fahndung"] = Wanted,
            ["/abmeldungen"] = Absences,
            ["/bewerbungen"] = Recruiting,
            ["/statistik"] = Statistics,
            ["/nachweis"] = Monitoring,
            ["/finanzierungen"] = Financing,
        };
}
