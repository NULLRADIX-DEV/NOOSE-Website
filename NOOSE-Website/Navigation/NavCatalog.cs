using Microsoft.AspNetCore.Components.Routing;
using MudBlazor;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Navigation;

/// <summary>Single source of truth for drawer, app bar menus, palette and breadcrumbs.</summary>
public static class NavCatalog
{
    /// <summary>All internal-agent entries in default order. Keys are stable; favorites/hidden/order reference them.</summary>
    public static readonly IReadOnlyList<NavEntry> Internal = new[]
    {
        new NavEntry("dashboard", "/dashboard", Icons.Material.Filled.SpaceDashboard, "Dashboard", NavSection.Primary, NavArea.Primary, NavLinkMatch.All,
            Description: "Lagezentrum mit Kennzahlen und letzten Änderungen"),
        // the app bar field and Ctrl+K already lead here
        new NavEntry("suche", "/suche", Icons.Material.Filled.Search, "Globale Suche", NavSection.Primary, NavArea.Primary,
            Description: "Durchsucht alle Akten, Dokumente und Inhalte", InDrawer: false),

        new NavEntry("personen", "/personen", Icons.Material.Filled.Badge, "Personen-Akten", NavSection.Akten, NavArea.Akten,
            Description: "Zentrale Akte je Person — alles zu einer Person an einem Ort"),
        new NavEntry("fraktionen", "/fraktionen", Icons.Material.Filled.Groups, "Fraktionen", NavSection.Akten, NavArea.Akten,
            Description: "Akten organisierter Gruppierungen mit Mitgliedern und Rängen"),
        new NavEntry("personengruppen", "/personengruppen", Icons.Material.Filled.Diversity3, "Personengruppen", NavSection.Akten, NavArea.Akten,
            Description: "Lose Zusammenschlüsse ohne feste Fraktionsstruktur"),
        new NavEntry("parteien", "/parteien", Icons.Material.Filled.AccountBalance, "Parteien", NavSection.Akten, NavArea.Akten,
            Description: "Politische Parteien mit Mitgliedern und Ausrichtung"),
        new NavEntry("drogenrouten", "/drogenrouten", Icons.Material.Filled.Route, "Drogenrouten", NavSection.Akten, NavArea.Akten,
            Description: "Routen des Drogenhandels mit Stationen, Beweisfotos und beteiligten Personen"),
        new NavEntry("vorgaenge", "/vorgaenge", Icons.Material.Filled.FolderSpecial, "Vorgänge", NavSection.VorgaengeEinsaetze, NavArea.Akten,
            Description: "Sammelakte, die mehrere Akten bündelt"),

        new NavEntry("ermittlungen", "/ermittlungen", Icons.Material.Filled.Gavel, "Ermittlungen", NavSection.VorgaengeEinsaetze, NavArea.Ermittlung,
            Description: "Ermittlungsakten mit der DoJ: Tatvorwurf, Zeugen, Beweise, Vernehmungen, nur für Beteiligte"),
        new NavEntry("operationen", "/operationen", Icons.Material.Filled.Radar, "Operationen", NavSection.VorgaengeEinsaetze, NavArea.Ermittlung,
            Description: "Einsatzbericht zu einem konkreten Einsatz mit Ort und Zeit"),
        new NavEntry("einsatzalarme", "/einsatzalarme", Icons.Material.Filled.NotificationsActive, "Einsatzalarme", NavSection.VorgaengeEinsaetze, NavArea.Ermittlung,
            Description: "Kurzfristig Agenten zusammenrufen und sehen, wer kommt"),
        new NavEntry("taskforces", "/taskforces", Icons.Material.Filled.Groups2, "Taskforces", NavSection.VorgaengeEinsaetze, NavArea.Ermittlung,
            Description: "Einheit aus Agenten mit Auftrag und Genehmigung"),
        new NavEntry("fahndung", "/fahndung", Icons.Material.Filled.PersonSearch, "Fahndung & Überwachung", NavSection.Fahndung, NavArea.Ermittlung,
            Description: "Gesuchte Personen, Observationen und Vernehmungen"),
        // key is not "hinweise": that one belongs to the algorithmic Ermittlungshinweise, and stored
        // favourites reference it
        new NavEntry("buergerhinweise", "/hinweise", Icons.Material.Filled.TipsAndUpdates, "Bürgerhinweise",
            NavSection.Fahndung, NavArea.Ermittlung, BadgeKey: "tips",
            Description: "Meldungen aus der Bevölkerung: Eingang, Bearbeitung und Rückfragen"),
        new NavEntry("informanten", "/informanten", Icons.Material.Filled.VisibilityOff, "Informanten", NavSection.Fahndung, NavArea.Ermittlung,
            Description: "Vertrauliche Quellen (V-Personen) mit Treffen-Protokollen"),
        new NavEntry("entfuehrungen", "/entfuehrungen", Icons.Material.Filled.PersonOff, "Entführungen", NavSection.Fahndung, NavArea.Ermittlung,
            Description: "Entführungen von FIB-Agenten: Täter, Informationsabfluss und kompromittierte Akten"),
        new NavEntry("anordnungen", "/anordnungen", Icons.Material.Filled.Approval, "Anordnungen", NavSection.Fahndung, NavArea.Ermittlung,
            Description: "Durchsuchungs-, Beschlagnahme- und Festnahmeanordnungen beantragen, unterzeichnen und drucken"),
        new NavEntry("asservatenkammer", "/asservatenkammer", Icons.Material.Filled.Inventory2, "Asservatenkammer", NavSection.Akten, NavArea.Ermittlung,
            Description: "Ein- und Auslagerung von Gegenständen mit Bestand, Bild und Besitzer"),

        new NavEntry("graph", "/graph", Icons.Material.Filled.Hub, "Beziehungsgraph", NavSection.Analyse, NavArea.Analyse,
            Description: "Verknüpfungen zwischen Akten als Netzdiagramm"),
        new NavEntry("hinweise", "/ermittlungshinweise", Icons.Material.Filled.Lightbulb, "Ermittlungshinweise", NavSection.Analyse, NavArea.Analyse,
            Description: "Algorithmische Hinweise: mögliche Verbindungen, Konflikte, veraltete Einstufungen"),
        // key stays "ki": stored favourites and hidden-entry settings reference it
        new NavEntry("ki", "/ki-assistent", Icons.Material.Filled.SmartToy, "FIBI", NavSection.Analyse, NavArea.Analyse,
            Description: "Fragen zur Aktenlage mit Zugriff auf die Aktendatenbank, plus Schreibhilfe"),
        new NavEntry("statistik", "/statistik", Icons.Material.Filled.QueryStats, "Statistik", NavSection.Analyse, NavArea.Analyse,
            Description: "Auswertungen, Kennzahlen und Lageberichte"),
        // key stays "chronik": stored favorites reference it, and LegacyRoutes aliases onto it
        new NavEntry("chronik", "/nachweis", Icons.Material.Filled.FactCheck, "Nachweis & Gegenaufklärung", NavSection.Analyse, NavArea.Analyse,
            Description: "Chronik, Änderungs- und Zugriffsprotokoll, Gegenaufklärung"),

        new NavEntry("meine-schicht", "/meine-schicht", Icons.Material.Filled.WorkHistory, "Meine Schicht", NavSection.MeinDienst, NavArea.Dienststelle,
            Description: "Was heute an dir hängt: Aufgaben, Wiedervorlagen, Erwähnungen, Brett, Taskforces"),
        new NavEntry("brett", "/brett", Icons.Material.Filled.Campaign, "Schwarzes Brett", NavSection.Dienststelle, NavArea.Dienststelle, BadgeKey: "acknowledgments",
            Description: "Behördliche Ankündigungen, teils quittierungspflichtig"),
        new NavEntry("kalender", "/kalender", Icons.Material.Filled.CalendarMonth, "Kalender", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "Termine der Behörde in der Monatsansicht"),
        new NavEntry("besprechungen", "/besprechungen", Icons.Material.Filled.Groups, "Besprechungen", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "Dienstbesprechungen mit Tagesordnung und Protokoll"),
        new NavEntry("praesentationen", "/praesentationen", Icons.Material.Filled.CoPresent, "Präsentationen", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "Folien anlegen und live für die Agenten vorführen, die gerade online sind"),
        new NavEntry("aufgaben", "/aufgaben", Icons.Material.Filled.AssignmentTurnedIn, "Aufgaben-Board", NavSection.MeinDienst, NavArea.Dienststelle,
            Description: "To-dos des Teams als Board — für alle Agenten sichtbar"),
        new NavEntry("personal", "/personal", Icons.Material.Filled.People, "Personal", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "Agenten der Behörde mit Personalakte und Dienstgrad"),
        new NavEntry("organigramm", "/organigramm", Icons.Material.Filled.AccountTree, "Organigramm", NavSection.Analyse, NavArea.Dienststelle,
            Description: "Aufbau der Behörde nach Board, Abteilung und Dienstgrad"),
        new NavEntry("bestenliste", "/bestenliste", Icons.Material.Filled.EmojiEvents, "Bestenliste", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "Team-Ranking nach dokumentierter Ermittlungsarbeit"),
        new NavEntry("kasse", "/kasse", Icons.Material.Filled.AccountBalanceWallet, "Kasse", NavSection.Dienststelle, NavArea.Dienststelle,
            Description: "FIB-Kasse: Schwarz- und Grüngeld buchen, Vorlagen und Statistik"),
        new NavEntry("finanzierungen", "/finanzierungen", Icons.Material.Filled.RequestQuote, "Finanzierungen", NavSection.MeinDienst, NavArea.Dienststelle, BadgeKey: "financing",
            Description: "Finanzierungsanträge stellen und das eigene Monatsbudget einsehen"),
        new NavEntry("verpflegung", "/verpflegung", Icons.Material.Filled.Restaurant, "Verpflegung", NavSection.MeinDienst, NavArea.Dienststelle, BadgeKey: "food",
            Description: "Essen und Trinken anfordern und das eigene Wochenlimit einsehen"),
        new NavEntry("abmeldungen", "/abmeldungen", Icons.Material.Filled.EventBusy, "Abmeldungen", NavSection.MeinDienst, NavArea.Dienststelle,
            Description: "Eigene Abwesenheiten melden und einsehen"),
        new NavEntry("aktivitaeten", "/aktivitaeten", Icons.Material.Filled.Bolt, "Aktivitäten (Archiv)", NavSection.MeinDienst, NavArea.Dienststelle,
            Description: "Diensteinträge, die Agenten über sich selbst führen — für alle sichtbar"),
        new NavEntry("tests", "/tests", Icons.Material.Filled.Quiz, "Interne Tests", NavSection.MeinDienst, NavArea.Dienststelle,
            Description: "Zugewiesene Tests ablegen; Administration Division und Führung vergeben und korrigieren"),

        new NavEntry("admin.freigaben", "/admin/freigaben", Icons.Material.Filled.HowToReg, "Freigaben", NavSection.VerwaltungFreigaben, NavArea.Leitung, BadgeKey: "shares",
            Description: "Posteingang für Anträge, Registrierungen und Freigaben"),
        new NavEntry("bewerbungen", "/bewerbungen", Icons.Material.Filled.HowToReg, "Bewerbungen", NavSection.VerwaltungBewerbungen, NavArea.Leitung,
            Description: "Eingang, Sperren, Anschreiben-Vorlagen und Eignungstests"),
        new NavEntry("admin.agenten", "/admin/agenten", Icons.Material.Filled.ManageAccounts, "Agenten-Verwaltung", NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "Dienstgrad, Status und Rechte der Agenten setzen"),
        // the Administration lists the same inbox as one of its sections
        new NavEntry("tickets", "/tickets", Icons.Material.Filled.Forum, "Bürger-Tickets",
            NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "Anliegen von Bürgern an die Führungsebene: Schriftwechsel und Abschluss",
            DrawerTwinOf: "administration.buergertickets"),
        new NavEntry("schusswaffengebrauch", "/schusswaffengebrauch", Icons.Material.Filled.GpsFixed, "Schusswaffengebrauch",
            NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "Gemeldeter Schusswaffengebrauch aller Agenten, filterbar nach Agent, Abteilung und Zeitraum",
            DrawerTwinOf: "administration.schusswaffengebrauch"),
        new NavEntry("wiedervorlagen.rueckstand", FollowupPlanRules.BacklogRoute, Icons.Material.Filled.PendingActions, "Wiedervorlagen-Rückstand",
            NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "Alle offenen Wiedervorlagen der Behörde: wer was liegen lässt, und umhängen"),
        new NavEntry("einstellungen", "/einstellungen", Icons.Material.Filled.SettingsApplications, "Einstellungen", NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "System, Vorlagen, Tags, Score, Partner und Protokoll an einer Stelle"),
        new NavEntry("papierkorb", "/papierkorb", Icons.Material.Filled.Delete, "Papierkorb", NavSection.VerwaltungFuehrung, NavArea.Leitung,
            Description: "Gelöschte Akten aller Typen wiederherstellen"),

        new NavEntry("profil", "/profil", Icons.Material.Filled.AccountCircle, "Mein Profil", NavSection.MeinDienst, NavArea.Ich,
            Description: "Eigene Stammdaten, Codename und Kontaktangaben"),
        new NavEntry("benachrichtigungen", "/benachrichtigungen", Icons.Material.Filled.Notifications, "Benachrichtigungen", NavSection.MeinDienst, NavArea.Ich,
            Description: "Alle eigenen Benachrichtigungen — filtern, blättern, wieder als ungelesen markieren"),
        new NavEntry("watchlist", "/watchlist", Icons.Material.Filled.Star, "Beobachtete Akten", NavSection.MeinDienst, NavArea.Ich,
            Description: "Akten, denen du folgst — mit Änderungshinweis"),
        new NavEntry("wiedervorlagen", FollowupPlanRules.Route, Icons.Material.Filled.EventRepeat, "Meine Wiedervorlagen", NavSection.MeinDienst, NavArea.Ich,
            Description: "Deine offenen Wiedervorlagen nach Fälligkeit: verschieben und erledigen mit einem Klick"),
        // the same page, a different section: without an entry of their own an attached agent could only ever
        // reach their tickets through a notification link
        new NavEntry("tickets.beteiligt", "/tickets?tab=beteiligt", Icons.Material.Filled.Group, "Meine Tickets",
            NavSection.MeinDienst, NavArea.Ich, NavLinkMatch.All,
            Description: "Tickets, an denen du beteiligt bist, und eigene Anliegen an die Führung"),

        new NavEntry("handbuch", "/handbuch", Icons.Material.Filled.MenuBook, "Handbuch", NavSection.Wissen, NavArea.Hilfe,
            Description: "Anleitungen und Glossar: wie die Seite und die Abläufe funktionieren"),
        new NavEntry("neuerungen", "/neuerungen", Icons.Material.Filled.NewReleases, "Neuerungen", NavSection.Wissen, NavArea.Hilfe,
            Description: "Was sich auf dieser Seite geändert hat, nach Fassungen"),
        new NavEntry("gesetze", "/gesetze", Icons.Material.Filled.Gavel, "Gesetzbuch", NavSection.Wissen, NavArea.Hilfe,
            Description: "Paragrafen und Rechtsgrundlagen zum Nachschlagen"),
        new NavEntry("dienstverordnung", "/dienstverordnung", Icons.Material.Filled.Gavel, "Dienstverordnung", NavSection.Wissen, NavArea.Hilfe,
            BadgeKey: "regulation", Description: "Die Regeln des FIB mit Abschnitten, Fassungen und Kenntnisnahme"),
        new NavEntry("funk", "/funk", Icons.Material.Filled.SettingsInputAntenna, "Funkplan", NavSection.Wissen, NavArea.Hilfe,
            Description: "Wer auf welchem Kanal funkt, und zu wem eine aufgeschnappte Frequenz gehört"),
        new NavEntry("dokumente", "/dokumente", Icons.Material.Filled.MenuBook, "Dokumenten-Bibliothek", NavSection.Wissen, NavArea.Hilfe,
            Description: "Zentrale Ablage aller behördlichen Dokumente"),
        new NavEntry("feedback", "/feedback", Icons.Material.Filled.Feedback, "Feedback", NavSection.MeinDienst, NavArea.Hilfe, Description: "Feedback zur Website"),
    }.Concat(AdministrationSections.All.Select(s => new NavEntry(AdministrationSections.Key(s), AdministrationSections.Route(s),
        s.Slug.Length == 0 ? Icons.Material.Filled.AssuredWorkload : s.Icon,
        // the start is the chat, but in drawer and search it stands for the whole workspace
        s.Slug.Length == 0 ? "Administration" : s.Label, NavSection.Administration, NavArea.Leitung,
        // the start route is a prefix of every section, so it matches exactly
        s.Slug.Length == 0 ? NavLinkMatch.All : NavLinkMatch.Prefix,
        BadgeKey: s.Slug.Length == 0 ? "administration-chat" : null, Description: s.Description, Group: s.Group,
        // the workspace lists its own sections
        InDrawer: s.Slug.Length == 0)))
        .ToArray();

    private static readonly Dictionary<string, NavEntry> ByKeyMap =
        Internal.ToDictionary(e => e.Key, StringComparer.Ordinal);

    /// <summary>Entries of one section, in catalog order.</summary>
    public static IEnumerable<NavEntry> Section(NavSection section)
        => Internal.Where(e => e.Section == section);

    /// <summary>Catalog entry by stable key, or null.</summary>
    public static NavEntry? ByKey(string key) => ByKeyMap.GetValueOrDefault(key);

    /// <summary>Catalog entry whose route best matches a relative path (longest prefix wins), or null.</summary>
    public static NavEntry? ByRoute(string? relativePath)
    {
        var path = (relativePath ?? string.Empty).Split('?')[0].Split('#')[0].Trim('/').ToLowerInvariant();
        if (path.Length == 0)
        {
            return ByKeyMap.GetValueOrDefault("dashboard");
        }

        NavEntry? best = null;
        var bestLen = -1;
        foreach (var e in Internal)
        {
            var route = e.Route.Trim('/').ToLowerInvariant();
            if (route.Length == 0)
            {
                continue;
            }
            if ((path == route || path.StartsWith(route + "/", StringComparison.Ordinal)) && route.Length > bestLen)
            {
                best = e;
                bestLen = route.Length;
            }
        }
        return best;
    }

    /// <summary>Partner record-type entries, filtered by the rank's allowed types (null = all), then the agency's function pages.</summary>
    public static IReadOnlyList<NavEntry> PartnerRecordEntries(IReadOnlySet<string>? allowedTypes, PartnerFeature features = PartnerFeature.None)
    {
        var list = new List<NavEntry>();
        foreach (var t in PartnerTabCatalog.All)
        {
            if (allowedTypes is null || allowedTypes.Contains(t.TypeKey))
            {
                list.Add(new NavEntry("partner." + t.TypeKey, "/" + t.RoutePrefix, PartnerIcon(t.TypeKey), t.DisplayName, NavSection.Partner, NavArea.Partner));
            }
        }
        // participation, not a release: the policy adds the key when the account sees or may open one
        if (allowedTypes?.Contains(InvestigationTypeKey) == true)
        {
            list.Add(new NavEntry("partner.Investigation", "/ermittlungen", Icons.Material.Filled.Gavel, "Ermittlungen", NavSection.Partner, NavArea.Partner));
        }
        if (allowedTypes?.Contains(ParliamentTypeKey) == true)
        {
            list.Add(new NavEntry("partner.parlament", "/parlament", Icons.Material.Filled.AccountBalance, "Parlamentarische Anfragen", NavSection.Partner, NavArea.Partner));
        }
        foreach (var f in PartnerFeaturePages.Where(f => features.HasFlag(f.Feature)))
        {
            list.Add(new NavEntry(f.Key, f.Href, f.Icon, f.Label, NavSection.Partner, NavArea.Partner));
        }
        return list;
    }

    /// <summary>Type key of the investigation file in a partner's allowed set.</summary>
    public const string InvestigationTypeKey = "Investigation";

    /// <summary>Type key of the parliamentary inquiries in a parliament account's allowed set.</summary>
    public const string ParliamentTypeKey = "ParliamentaryInquiry";

    private static readonly (PartnerFeature Feature, string Key, string Href, string Icon, string Label)[] PartnerFeaturePages =
    {
        (PartnerFeature.Noosei, "partner.ki", "/ki-assistent", Icons.Material.Filled.Gavel, "FIBI-Rechtsauskunft"),
        (PartnerFeature.Graph, "partner.graph", "/graph", Icons.Material.Filled.Hub, "Beziehungsgraph"),
        (PartnerFeature.Radio, "partner.funk", "/funk", Icons.Material.Filled.SettingsInputAntenna, "Funkplan"),
        (PartnerFeature.SituationReports, "partner.lageberichte", "/lageberichte/archiv", Icons.Material.Filled.Assessment, "Lageberichte"),
        (PartnerFeature.ShareRequests, "partner.anfragen", "/anfragen", Icons.Material.Filled.RequestPage, "Freigaben anfragen"),
    };

    /// <summary>Icon per releasable record type.</summary>
    public static string PartnerIcon(string typeKey) => typeKey switch
    {
        nameof(Person) => Icons.Material.Filled.Badge,
        nameof(Faction) => Icons.Material.Filled.Groups,
        nameof(PersonGroup) => Icons.Material.Filled.Diversity3,
        nameof(Party) => Icons.Material.Filled.AccountBalance,
        nameof(DrugRoute) => Icons.Material.Filled.Route,
        nameof(Operation) => Icons.Material.Filled.Radar,
        nameof(Taskforce) => Icons.Material.Filled.Groups2,
        nameof(Case) => Icons.Material.Filled.FolderSpecial,
        nameof(Document) => Icons.Material.Filled.MenuBook,
        nameof(Law) => Icons.Material.Filled.Gavel,
        _ => Icons.Material.Filled.Folder,
    };
}
