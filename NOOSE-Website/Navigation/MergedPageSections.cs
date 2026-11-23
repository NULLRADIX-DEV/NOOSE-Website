namespace NOOSE_Website.Navigation;

/// <summary>Section slugs of each fixed page with a section rail.</summary>
public static class MergedPageSections
{
    public static readonly string[] Settings =
    [
        "system", "discord", "discord-rollen", "discord-direktnachrichten", "einsatzalarm", "status", "neuerungen",
        "custom-felder", "tags", "aktualitaet", "bedrohungs-score",
        "vorlagen-dok", "vorlagen-dokument", "vorlagen-aktivitaet", "vorlagen-personal",
        "module", "einladungen", "ehrenzeichen", "inaktivitaet", "bester-agent",
        "finanzierung",
        "verpflegung",
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
        "personen", "fraktionen", "personengruppen", "parteien", "drogenrouten",
        "vorgaenge", "operationen", "taskforces", "aufgaben",
        "brett", "kalender", "besprechungen", "aktivitaeten", "abmeldungen", "entfuehrungen",
        "schusswaffengebrauch",
        "disziplinarverfahren", "dienstzeugnisse", "parlamentarische-anfragen",
        "praesentationen", "informanten", "funk",
        "asservate-items", "asservate-eintraege", "kasse-buchungen", "finanzierungen", "verpflegung",
        "anordnungen", "ermittlungen",
        "feedback", "oeffentliche-seiten", "oeffentliche-fahndungen", "oeffentliche-fraktionsprofile",
        "hinweise", "tickets", "fahndungs-einsprueche", "pressemitteilungen", "oeffentliche-warnungen", "oeffentliche-lageberichte",
        "neuerungen", "handbuch-kapitel", "handbuch-artikel", "gesetzbuecher", "gesetze", "dienstverordnung",
    ];

    public static readonly string[] Wanted =
        ["fahndung", "oeffentlich", "organisationen", "einsprueche", "observationen", "doks"];

    public static readonly string[] Absences = ["meine", "uebersicht", "papierkorb"];

    public static readonly string[] Recruiting = ["eingang", "sperren", "vorlagen", "tests", "anforderungen", "automatik"];

    public static readonly string[] Statistics =
    [
        "ueberblick", "bestand", "aktivitaet", "bedrohung", "entfuehrungen", "netzwerk", "kasse", "finanzierung", "verpflegung", "dienststelle", "nutzung", "lageberichte",
    ];

    public static readonly string[] Financing =
    [
        "meine", "alle", "katalog", "budgets", "statistik", "papierkorb",
    ];

    public static readonly string[] Food =
    [
        "meine", "alle", "katalog", "limits", "statistik", "papierkorb",
    ];

    public static readonly string[] Cash =
    [
        "schwarzgeld", "gruengeld", "kopfgeld", "vorlagen", "statistik", "papierkorb",
    ];

    public static readonly string[] Personnel = ["agenten", "organigramm", "bestenliste"];

    public static readonly string[] Tests = ["meine", "korrektur", "katalog"];

    public static readonly string[] Tickets = ["offen", "bearbeitung", "wartet", "geschlossen", "beteiligt"];

    public static readonly string[] Feedback = ["melden", "meine", "eingang"];

    // chapters from data
    public static readonly string[] Handbook = ["glossar"];

    // sections from data
    public static readonly string[] Regulation = ["aufgehoben", "papierkorb"];

    public static readonly string[] Abductions = ["uebersicht", "kompromittiert", "papierkorb"];

    public static readonly string[] Evidence = ["bestand", "eintraege", "papierkorb"];

    public static readonly string[] Warrants = ["uebersicht", "papierkorb"];

    public static readonly string[] Tips = ["eingang", "bearbeitung", "erledigt"];

    public static readonly string[] KiAssistant = ["chat", "kontingent"];

    /// <summary>Section slugs of a fixed rail page, keyed by its route.</summary>
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
            ["/verpflegung"] = Food,
            ["/personal"] = Personnel,
            ["/kasse"] = Cash,
            ["/tests"] = Tests,
            ["/tickets"] = Tickets,
            ["/feedback"] = Feedback,
            ["/handbuch"] = Handbook,
            ["/dienstverordnung"] = Regulation,
            ["/entfuehrungen"] = Abductions,
            ["/asservatenkammer"] = Evidence,
            ["/anordnungen"] = Warrants,
            ["/hinweise"] = Tips,
            ["/ki-assistent"] = KiAssistant,
        };
}
