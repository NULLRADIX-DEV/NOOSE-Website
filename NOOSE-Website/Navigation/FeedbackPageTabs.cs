namespace NOOSE_Website.Navigation;

/// <summary>One selectable tab of a merged hub page.</summary>
public sealed record FeedbackTab(string Slug, string Label);

/// <summary>Tab catalog of the merged hub pages for the feedback page/tab picker; mirrors the RecordSection declarations.</summary>
public static class FeedbackPageTabs
{
    public static readonly FeedbackTab[] Settings =
    [
        new("system", "System & Erscheinungsbild"),
        new("discord", "Discord-Benachrichtigungen"),
        new("bester-agent", "Bester Agent"),
        new("neuerungen", "Neuerungen"),
        new("status", "System-Status"),
        new("custom-felder", "Custom-Felder"),
        new("tags", "Tags"),
        new("aktualitaet", "Aktualitäts-Ampel"),
        new("bedrohungs-score", "Bedrohungs-Score"),
        new("vorlagen-dok", "Dok-Vorlagen"),
        new("vorlagen-dokument", "Dokument-Vorlagen"),
        new("vorlagen-aktivitaet", "Aktivitäts-Vorlagen"),
        new("vorlagen-personal", "Personal-Vorlagen"),
        new("module", "Ausbildungsmodule"),
        new("einladungen", "Einladungen"),
        new("ehrenzeichen", "Ehrenzeichen"),
        new("inaktivitaet", "Inaktivitäts-Radar"),
        new("finanzierung", "Budget-Regeln"),
        new("noosei", "NOOSEI-Betrieb"),
        new("ki-anbieter", "Anbieter"),
        new("ki-regeln", "Token-Regeln"),
        new("ki-betrieb", "Betriebszahlen"),
        new("ki-kontingente", "Kontingente"),
        new("ki-anfragen", "NOOSEI-Anfragen"),
        new("partner", "Sichtbarkeit & Freigaben"),
        new("oeffentliche-module", "Module & Not-Aus"),
        new("oeffentliche-seiten", "Öffentliche Seiten"),
        new("oeffentliche-faq", "Häufige Fragen"),
        new("presse", "Pressemitteilungen"),
        new("lage", "Gefahrenlage"),
        new("warnungen", "Öffentliche Warnungen"),
        new("berichte", "Öffentliche Lageberichte"),
        new("recht", "Gesetzesauszüge"),
        new("oeffentliche-kennzahlen", "Kennzahlen des öffentlichen Bereichs"),
        new("oeffentliche-fuehrung", "Öffentliches Organigramm der Führung"),
        new("warnhinweise", "Warnhinweise"),
        new("oeffentliche-vorlagen", "Öffentliche Vorlagen"),
        new("buerger", "Bürgerkonten"),
        new("basisdaten", "Wertelisten"),
    ];

    public static readonly FeedbackTab[] Wanted =
    [
        new("fahndung", "Fahndung"),
        new("oeffentlich", "Öffentliche Ausschreibungen"),
        new("organisationen", "Öffentliche Organisationsprofile"),
        new("einsprueche", "Einsprüche"),
        new("observationen", "Observationen"),
        new("doks", "Vernehmungen & Maßnahmen"),
    ];

    public static readonly FeedbackTab[] Absences =
    [
        new("meine", "Meine Abmeldungen"),
        new("uebersicht", "Anwesenheit"),
        new("papierkorb", "Papierkorb"),
    ];

    public static readonly FeedbackTab[] Recruiting =
    [
        new("eingang", "Eingang"),
        new("sperren", "Sperren"),
        new("vorlagen", "Anschreiben-Vorlagen"),
        new("tests", "Eignungstests"),
        new("anforderungen", "Anforderungen"),
        new("automatik", "Automatik"),
    ];

    public static readonly FeedbackTab[] Statistics =
    [
        new("ueberblick", "Überblick"),
        new("bestand", "Bestand"),
        new("aktivitaet", "Aktivität"),
        new("bedrohung", "Bedrohungslage"),
        new("entfuehrungen", "Entführungen"),
        new("netzwerk", "Netzwerk"),
        new("kasse", "Kasse"),
        new("finanzierung", "Finanzierungen"),
        new("dienststelle", "Dienststelle"),
        new("lageberichte", "Lageberichte"),
    ];

    public static readonly FeedbackTab[] Monitoring =
    [
        new("chronik", "Chronik"),
        new("aenderungen", "Änderungen"),
        new("zugriffe", "Zugriffe"),
        new("lagebild", "Lagebild"),
        new("agenten-profil", "Agenten-Profil"),
        new("auffaelligkeiten", "Auffälligkeiten"),
        new("regeln", "Regeln"),
    ];

    public static readonly FeedbackTab[] Financing =
    [
        new("meine", "Meine Anträge"),
        new("alle", "Alle Anträge"),
        new("katalog", "Katalog"),
        new("budgets", "Budgets"),
        new("statistik", "Statistik"),
        new("papierkorb", "Papierkorb"),
    ];

    public static readonly FeedbackTab[] Kasse =
    [
        new("schwarzgeld", "Schwarzgeld"),
        new("gruengeld", "Grüngeld"),
        new("vorlagen", "Vorlagen"),
        new("statistik", "Statistik"),
        new("papierkorb", "Papierkorb"),
    ];

    public static readonly FeedbackTab[] Abductions =
    [
        new("uebersicht", "Übersicht"),
        new("kompromittiert", "Kompromittierte Akten"),
        new("papierkorb", "Papierkorb"),
    ];

    public static readonly FeedbackTab[] Evidence =
    [
        new("bestand", "Bestand"),
        new("eintraege", "Einträge"),
        new("papierkorb", "Papierkorb"),
    ];

    /// <summary>Tabs of a merged hub page, keyed by its route; /papierkorb stays out (runtime kinds from ITrashService).</summary>
    public static readonly IReadOnlyDictionary<string, FeedbackTab[]> ByRoute =
        new Dictionary<string, FeedbackTab[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["/einstellungen"] = Settings,
            ["/fahndung"] = Wanted,
            ["/abmeldungen"] = Absences,
            ["/bewerbungen"] = Recruiting,
            ["/statistik"] = Statistics,
            ["/nachweis"] = Monitoring,
            ["/finanzierungen"] = Financing,
            ["/kasse"] = Kasse,
            ["/entfuehrungen"] = Abductions,
            ["/asservatenkammer"] = Evidence,
        };

    /// <summary>Tabs of the given route, empty when the page has no tab rail.</summary>
    public static IReadOnlyList<FeedbackTab> TabsFor(string? route)
        => route is not null && ByRoute.TryGetValue(route, out var tabs) ? tabs : [];
}
