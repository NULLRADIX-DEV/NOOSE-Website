using MudBlazor;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>One selectable icon for a module override.</summary>
public sealed record PublicIconChoice(string Name, string Label, string Icon);

/// <summary>The catalog of public modules: the single place that decides which switches exist.</summary>
/// <remarks>
/// Keys for modules that are not built yet are listed here from the start, so the settings page shows the full shape
/// of the public area and an operator can turn something off before it ever goes live. <c>Available</c> says whether
/// the pages behind a key exist in this build; the switch is stored either way.
/// </remarks>
public static class PublicModules
{
    // --- wanted ---
    public const string Wanted = "Fahndung";
    public const string WantedVehicles = "FahndungFahrzeuge";
    public const string WantedArchive = "FahndungArchiv";
    public const string WantedPrint = "FahndungDruck";
    public const string Bounty = "Kopfgeld";
    public const string HazardLists = "Gefahrenlisten";
    public const string Organisations = "Organisationen";

    // --- agency ---
    public const string Careers = "Karriere";
    public const string InfoPages = "Infoseiten";
    public const string Faq = "Faq";
    public const string Press = "Presse";
    public const string SituationReports = "Lageberichte";
    public const string Warnings = "Warnungen";
    public const string Law = "Recht";
    public const string HazardLevel = "Gefahrenlage";
    public const string Statistics = "Statistik";
    public const string Leadership = "Fuehrung";

    // --- service ---
    public const string Tips = "Hinweise";
    public const string Reward = "Belohnung";
    public const string Tickets = "Tickets";
    public const string CaptureReports = "Ergreifungsmeldung";
    public const string Objection = "Einspruch";
    public const string CitizenRegistration = "BuergerRegistrierung";
    public const string PublicSearch = "OeffentlicheSuche";

    private const string OffWanted = "Die öffentliche Fahndung ist derzeit nicht verfügbar.";
    private const string OffGeneric = "Dieser Bereich ist derzeit nicht verfügbar.";

    public static readonly IReadOnlyList<PublicModuleDefinition> All =
    [
        new(Wanted, "Gesucht", "Öffentliches Fahndungsboard mit Steckbriefen.",
            Icons.Material.Filled.PersonSearch, "/gesucht", PublicModuleGroup.Fahndung, 10, false, true, OffWanted),
        // no nav route: the item notices share the board and its kind filter with the person notices, so a tab of
        // its own would be a second truth about the same page. A sub-switch of Fahndung, like Kopfgeld.
        new(WantedVehicles, "Gesuchte Fahrzeuge & Waffen", "Ausschreibungen ohne Personenbezug (Kennzeichen, Waffen).",
            Icons.Material.Filled.DirectionsCar, null, PublicModuleGroup.Fahndung, 15, false, true, OffWanted),
        new(WantedArchive, "Gefasst", "Archiv abgeschlossener Ausschreibungen.",
            Icons.Material.Filled.Inventory2, "/gefasst", PublicModuleGroup.Fahndung, 20, false, true, OffGeneric),
        // no nav route: the poster is reached from a profile, not from a tab
        new(WantedPrint, "Fahndungsposter", "Druckansicht eines Steckbriefs zum Aushängen.",
            Icons.Material.Filled.Print, null, PublicModuleGroup.Fahndung, 25, false, true, OffGeneric),
        // no nav route: a bounty is read at a profile, not from a tab of its own
        new(Bounty, "Kopfgeld", "Anzeige der ausgeschriebenen Belohnung an einem Steckbrief.",
            Icons.Material.Filled.Paid, null, PublicModuleGroup.Fahndung, 30, false, true, OffGeneric),
        new(HazardLists, "Gefahrenlisten", "Ranglisten der gefährlichsten Organisationen und Personen.",
            Icons.Material.Filled.Warning, "/gefahr/fraktionen", PublicModuleGroup.Fahndung, 40, false, true, OffGeneric),
        new(Organisations, "Organisationen", "Öffentliche Profile beobachteter und verbotener Organisationen.",
            Icons.Material.Filled.Groups, "/organisationen", PublicModuleGroup.Fahndung, 50, false, true, OffGeneric),

        new(Careers, "Karriere", "Informationen zum Auswahlverfahren und der Bewerbungs-Zugang.",
            Icons.Material.Filled.WorkOutline, "/karriere", PublicModuleGroup.Behoerde, 100, true, true,
            "Wir nehmen derzeit keine Bewerbungen an."),
        new(InfoPages, "Information", "Redaktionelle Seiten zu Auftrag, Befugnissen und Zuständigkeiten.",
            Icons.Material.Filled.MenuBook, "/info", PublicModuleGroup.Behoerde, 110, false, true,
            "Die Informationsseiten sind derzeit nicht verfügbar."),
        // a switch of its own, not a corner of Information: the FAQ answers what people ask before they write to
        // us, so it has to be closable — and openable — without touching the editorial pages next to it
        new(Faq, "Häufige Fragen", "Gegliedertes FAQ mit Rubriken und aufklappbaren Fragen auf /faq.",
            Icons.Material.Filled.QuestionAnswer, "/faq", PublicModuleGroup.Behoerde, 115, false, true,
            "Das FAQ ist derzeit nicht verfügbar."),
        new(Press, "Presse", "Pressemitteilungen der Behörde.",
            Icons.Material.Filled.Feed, "/presse", PublicModuleGroup.Behoerde, 120, false, true, OffGeneric),
        // the ONE outward surface that names agents, and it does so automatically: switching it on publishes the
        // real name of every selectable agent at once. Off by default like everything else.
        new(Leadership, "Mitarbeiter", "Organigramm der Behördenleitung mit Foto und Telefon, dazu alle Agenten mit Namen.",
            Icons.Material.Filled.Groups, "/fuehrung", PublicModuleGroup.Behoerde, 125, false, true,
            OffGeneric),
        // /berichte, not /lageberichte: that route is internal (the legacy redirect and the leadership-only print page
        // with the classified aggregates). Because Prefixes collects the nav routes without asking Available, naming it
        // here declared an internal page indexable
        new(SituationReports, "Lageberichte", "Von der Führung freigegebene Monatstexte.",
            Icons.Material.Filled.Assessment, "/berichte", PublicModuleGroup.Behoerde, 130, false, true, OffGeneric),
        new(Warnings, "Warnungen", "Amtliche Warnungen mit Gültigkeitsdatum.",
            Icons.Material.Filled.Campaign, "/warnungen", PublicModuleGroup.Behoerde, 140, false, true, OffGeneric),
        new(Law, "Recht", "Öffentlich freigegebene Gesetzesauszüge.",
            Icons.Material.Filled.Gavel, "/recht", PublicModuleGroup.Behoerde, 150, false, true, OffGeneric),
        new(HazardLevel, "Lage", "Gefahrenlage-Stufe mit Einschätzung; die Stufe davor ist der Trend.",
            Icons.Material.Filled.Speed, "/lage", PublicModuleGroup.Behoerde, 160, false, true, OffGeneric),
        // no nav route: the figures are a band on the start page, not a page of their own — a tab would be a second
        // truth about the same content, the same reason Kopfgeld has none
        new(Statistics, "Zahlen", "Öffentliche Kennzahlen zu Ausschreibungen, Hinweisen und Belohnungen.",
            Icons.Material.Filled.BarChart, null, PublicModuleGroup.Behoerde, 170, false, true, OffGeneric),

        new(Tips, "Hinweis geben", "Formular zur Übermittlung von Hinweisen.",
            Icons.Material.Filled.TipsAndUpdates, "/hinweis", PublicModuleGroup.Service, 200, false, true,
            "Wir nehmen derzeit keine Hinweise über die Website an."),
        // no nav route: a receipt is opened from the tip it belongs to, not from a tab
        new(Reward, "Belohnung", "Auszahlung und Belege für bestätigte Hinweise.",
            Icons.Material.Filled.Redeem, null, PublicModuleGroup.Service, 210, false, true, OffGeneric),
        // no nav route: a ticket is opened inside the citizen area, not from a public tab
        new(Tickets, "Ticket-Chat", "Anliegen an die Führungsebene im Bürgerbereich.",
            Icons.Material.Filled.Forum, null, PublicModuleGroup.Service, 220, false, true,
            "Der Ticket-Chat ist derzeit geschlossen."),
        // no nav route: an objection is filed from the notice it disputes and read in the citizen area, not from
        // a public tab of its own
        new(Objection, "Einspruch", "Widerspruch gegen eine öffentliche Ausschreibung.",
            Icons.Material.Filled.Balance, null, PublicModuleGroup.Service, 230, false, true, OffGeneric),
        // no nav route, and here that is more than the usual reason: a report is filed from the notice it
        // concerns, and a tab inviting a fresh one with no notice in hand would read as an invitation to go and
        // catch somebody. The board keeps telling people not to intervene; this only records what already happened
        new(CaptureReports, "Person gestellt melden",
            "Meldung eines Bürgers, dass eine gesuchte Person bereits gestellt ist.",
            Icons.Material.Filled.LocalPolice, null, PublicModuleGroup.Service, 235, false, true,
            "Ergreifungsmeldungen nehmen wir derzeit nicht über die Website an."),
        new(CitizenRegistration, "Bürger-Registrierung", "Neuanmeldung eines Bürgerkontos über Discord. Bestehende Konten behalten ihren Zugang.",
            Icons.Material.Filled.Person, null, PublicModuleGroup.Service, 240, true, true,
            "Neue Bürgerkonten können derzeit nicht angelegt werden."),
        new(PublicSearch, "Suche", "Volltextsuche über Fahndungen, Organisationen, Pressemitteilungen, Warnungen, "
            + "Lageberichte, Infoseiten, häufige Fragen und freigegebene Gesetzesauszüge.",
            Icons.Material.Filled.Search, "/suche-oeffentlich", PublicModuleGroup.Service, 250, false, true, OffGeneric),
    ];

    /// <summary>Icon of an editorial page that picked none.</summary>
    public const string PageDefaultIcon = Icons.Material.Filled.MenuBook;

    /// <summary>Icons an operator may pick; stored by name so a module can never carry raw markup.</summary>
    public static readonly IReadOnlyList<PublicIconChoice> IconChoices =
    [
        new("PersonSearch", "Fahndung", Icons.Material.Filled.PersonSearch),
        new("Warning", "Warnung", Icons.Material.Filled.Warning),
        new("Shield", "Wappen", Icons.Material.Filled.Shield),
        new("Groups", "Organisation", Icons.Material.Filled.Groups),
        new("Person", "Person", Icons.Material.Filled.Person),
        new("Paid", "Geld", Icons.Material.Filled.Paid),
        new("Feed", "Presse", Icons.Material.Filled.Feed),
        new("Assessment", "Bericht", Icons.Material.Filled.Assessment),
        new("Gavel", "Recht", Icons.Material.Filled.Gavel),
        new("Campaign", "Durchsage", Icons.Material.Filled.Campaign),
        new("Forum", "Chat", Icons.Material.Filled.Forum),
        new("Tips", "Hinweis", Icons.Material.Filled.TipsAndUpdates),
        new("Search", "Suche", Icons.Material.Filled.Search),
        new("MenuBook", "Information", Icons.Material.Filled.MenuBook),
        new("BarChart", "Zahlen", Icons.Material.Filled.BarChart),
        new("Speed", "Lage", Icons.Material.Filled.Speed),
        new("Inventory2", "Archiv", Icons.Material.Filled.Inventory2),
        new("DirectionsCar", "Fahrzeug", Icons.Material.Filled.DirectionsCar),
        new("WorkOutline", "Karriere", Icons.Material.Filled.WorkOutline),
        new("Print", "Druck", Icons.Material.Filled.Print),
    ];

    public static PublicModuleDefinition? Find(string? key)
        => key is null ? null : All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));

    /// <summary>Resolves an override name to an icon; anything unknown falls back to the catalog icon.</summary>
    public static string IconFor(string? overrideName, string fallback)
    {
        if (string.IsNullOrWhiteSpace(overrideName))
        {
            return fallback;
        }
        var choice = IconChoices.FirstOrDefault(c => string.Equals(c.Name, overrideName, StringComparison.Ordinal));
        return choice?.Icon ?? fallback;
    }

    /// <summary>True when the name is one an operator may store.</summary>
    public static bool IsKnownIcon(string? name)
        => !string.IsNullOrWhiteSpace(name)
            && IconChoices.Any(c => string.Equals(c.Name, name, StringComparison.Ordinal));
}
