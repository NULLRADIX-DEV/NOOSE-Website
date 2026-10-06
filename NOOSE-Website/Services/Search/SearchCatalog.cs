using MudBlazor;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Feedback;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Taskforces;

namespace NOOSE_Website.Services.Search;

/// <summary>How categories are bundled in the result facet bar.</summary>
public enum SearchGroup
{
    Records,
    Content,
    Administration,
    Logs,
    Personal,
}

/// <summary>Which of the four row layouts renders a hit of this category.</summary>
public enum SearchHitShape
{
    Record,
    Content,
    Log,
    Personal,
}

/// <summary>What a search category can do.</summary>
/// <remarks>
/// Traits rather than one "searchable" flag, for the same reason <see cref="Llm.Tools.NooseiUse"/> has four axes:
/// a tag filter, a Levenshtein pass, the command palette, a longtext scan and a private inbox are separate
/// capabilities, and a single list would let the narrowest of them decide for all of them.
/// </remarks>
[Flags]
public enum SearchTraits
{
    None = 0,

    /// <summary>Carries rows in <c>TagMappings</c>, so a tag-scoped query can narrow it instead of skipping it.</summary>
    Tagged = 1,

    /// <summary>The provider supplies Levenshtein candidates on top of the LIKE recall.</summary>
    Fuzzy = 2,

    /// <summary>Offered in the Strg+K palette. Implies a route.</summary>
    /// <remarks>
    /// It does not exclude <see cref="Heavy"/>, and <c>Document</c> and <c>Meeting</c> carry both: the palette
    /// query and the full search are separate paths, so a category can be cheap to offer by name and expensive
    /// to scan by body. The claim that the two were mutually exclusive stood here for a while and was wrong.
    /// </remarks>
    Quick = 4,

    /// <summary>Scans a longtext column. Runs in the second budget wave, so an expired budget costs this rather
    /// than the record types the agent was actually looking for.</summary>
    Heavy = 8,

    /// <summary>A hit points at a parent record, never at itself; it carries the parent's type and id.</summary>
    ContentChild = 16,

    /// <summary>Rows are private to the viewer; the provider filters on <c>MeId</c> and nothing else reaches it.</summary>
    Personal = 32,

    /// <summary>Fed by <see cref="SearchIndexProjection"/>; the provider resolves side-index candidate ids.</summary>
    SideIndexed = 64,

    /// <summary>An external partner may see this category at all — released, non-classified rows only.</summary>
    Partner = 128,

    /// <summary>A category the assistant's <c>suche_akten</c> may narrow to.</summary>
    /// <remarks>Every category carries it: the assistant reads exactly what the asking agent may read, and each
    /// provider still gates itself. Narrowing was never the gate — hits from an unflagged category already reached
    /// the model on an unrestricted query, just without an id to follow. The flag stays a per-category decision so a
    /// single category can be withdrawn from the assistant without touching anything else.</remarks>
    Assistant = 256,
}

/// <summary>One searchable category: how it is named, where a hit leads, and what the provider behind it can do.</summary>
/// <param name="RouteTemplate">Href of a hit with <c>{0}</c> for the id. Null for a
/// <see cref="SearchTraits.ContentChild"/>, which is routed through the row of its parent type.</param>
/// <param name="ParentTab">Section slug appended as <c>?tab=</c> to the PARENT route of a content child.</param>
public sealed record SearchCategory(
    string Clr,
    string German,
    string Plural,
    SearchGroup Group,
    string Icon,
    SearchHitShape Shape,
    SearchTraits Traits,
    string? RouteTemplate,
    string? ParentTab = null)
{
    public bool Has(SearchTraits trait) => Traits.HasFlag(trait);
}

/// <summary>Every category the global search can produce.</summary>
/// <remarks>
/// The single source for the facet bar, the icons, the German labels, <see cref="Models.Common.SearchNavigation"/>,
/// the <c>suche_akten</c> schema enum and the coverage test. A new searchable type is one row here plus one
/// <see cref="ISearchProvider"/> — and the coverage test fails until both exist.
/// </remarks>
public static class SearchCatalog
{
    /// <summary>Order is load-bearing: it is the order of the facet bar and of the result groups. Append, never reorder.</summary>
    private static readonly SearchCategory[] All =
    [
        // ---- Akten ----
        new(nameof(Person), "Person", "Personen", SearchGroup.Records,
            Icons.Material.Filled.Badge, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/personen/{0}"),
        new(nameof(Faction), "Fraktion", "Fraktionen", SearchGroup.Records,
            Icons.Material.Filled.Groups, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/fraktionen/{0}"),
        new(nameof(PersonGroup), "Personengruppe", "Personengruppen", SearchGroup.Records,
            Icons.Material.Filled.Diversity3, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/personengruppen/{0}"),
        new(nameof(Party), "Partei", "Parteien", SearchGroup.Records,
            Icons.Material.Filled.AccountBalance, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/parteien/{0}"),
        new(nameof(Operation), "Operation", "Operationen", SearchGroup.Records,
            Icons.Material.Filled.Radar, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/operationen/{0}"),
        new(nameof(Case), "Vorgang", "Vorgänge", SearchGroup.Records,
            Icons.Material.Filled.FolderSpecial, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/vorgaenge/{0}"),
        new(nameof(Taskforce), "Taskforce", "Taskforces", SearchGroup.Records,
            Icons.Material.Filled.Groups2, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Partner | SearchTraits.Assistant,
            "/taskforces/{0}"),
        new(nameof(Job), "Aufgabe", "Aufgaben", SearchGroup.Records,
            Icons.Material.Filled.Assignment, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Quick | SearchTraits.SideIndexed
                | SearchTraits.Assistant,
            "/aufgaben/{0}"),
        new(nameof(Law), "Gesetz", "Gesetze", SearchGroup.Records,
            Icons.Material.Filled.Gavel, SearchHitShape.Record,
            SearchTraits.SideIndexed | SearchTraits.Partner | SearchTraits.Assistant,
            "/gesetze/{0}"),
        new(nameof(AgentAbduction), "Entführung", "Entführungen", SearchGroup.Records,
            Icons.Material.Filled.PersonOff, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/entfuehrungen/{0}"),
        new(nameof(EvidenceItem), "Asservat", "Asservate", SearchGroup.Records,
            Icons.Material.Filled.Inventory2, SearchHitShape.Record,
            SearchTraits.SideIndexed | SearchTraits.Assistant,
            "/asservatenkammer/item/{0}"),
        new(nameof(EvidenceEntry), "Asservat-Eintrag", "Asservat-Einträge", SearchGroup.Records,
            Icons.Material.Filled.Inventory, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/asservatenkammer/eintrag/{0}"),
        new(nameof(KassenBuchung), "Kassenbuchung", "Kassenbuchungen", SearchGroup.Records,
            Icons.Material.Filled.AccountBalanceWallet, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/kasse/buchung/{0}"),
        new(nameof(FinancingRequest), "Finanzierungsantrag", "Finanzierungsanträge", SearchGroup.Records,
            Icons.Material.Filled.RequestQuote, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/finanzierungen/{0}"),

        // ---- Inhalte ----
        new(nameof(AgentActivity), "Aktivität", "Aktivitäten", SearchGroup.Content,
            Icons.Material.Filled.Bolt, SearchHitShape.Record,
            SearchTraits.Tagged | SearchTraits.Fuzzy | SearchTraits.Heavy | SearchTraits.Assistant,
            "/aktivitaeten/{0}"),
        // the dok itself has no page: a hit targets the person whose file it sits in
        new(nameof(PersonDoc), "Personen-Dok", "Doks", SearchGroup.Content,
            Icons.Material.Filled.Description, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Assistant,
            null, "doks"),
        new(nameof(Source), "Quelle", "Quellen", SearchGroup.Content,
            Icons.Material.Filled.AttachFile, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Partner | SearchTraits.Assistant,
            null, "quellen"),
        new(nameof(Comment), "Kommentar", "Kommentare", SearchGroup.Content,
            Icons.Material.Filled.Comment, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Partner | SearchTraits.Assistant,
            null, "kommentare"),
        new(nameof(Document), "Dokument", "Dokumente", SearchGroup.Content,
            Icons.Material.Filled.MenuBook, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Quick | SearchTraits.Partner | SearchTraits.Assistant,
            "/dokumente/{0}"),
        new(nameof(Meeting), "Besprechung", "Besprechungen", SearchGroup.Content,
            Icons.Material.Filled.Groups3, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Quick | SearchTraits.Assistant,
            "/besprechungen/{0}"),
        // the item has no page; a hit opens the meeting's agenda section
        new(nameof(MeetingAgendaItem), "Tagesordnungspunkt", "Tagesordnungspunkte", SearchGroup.Content,
            Icons.Material.Filled.Checklist, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "tagesordnung"),
        new(nameof(Appointment), "Termin", "Termine", SearchGroup.Records,
            Icons.Material.Filled.Event, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/kalender/{0}"),
        new(nameof(Agent), "Personalakte", "Personalakten", SearchGroup.Records,
            Icons.Material.Filled.AssignmentInd, SearchHitShape.Record,
            SearchTraits.Quick | SearchTraits.SideIndexed | SearchTraits.Assistant,
            "/personal/{0}"),
        new(nameof(AgentNote), "Vermerk", "Vermerke", SearchGroup.Content,
            Icons.Material.Filled.EditNote, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "vermerke"),
        new(nameof(Announcement), "Ankündigung", "Ankündigungen", SearchGroup.Records,
            Icons.Material.Filled.Campaign, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant,
            "/brett/{0}"),
        new(nameof(Informant), "Informant", "Informanten", SearchGroup.Records,
            Icons.Material.Filled.VisibilityOff, SearchHitShape.Record,
            SearchTraits.Assistant,
            "/informanten/{0}"),
        new(nameof(InformantMeeting), "Informanten-Treffen", "Informanten-Treffen", SearchGroup.Content,
            Icons.Material.Filled.Handshake, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "treffen"),
        new(nameof(Observation), "Observation", "Observationen", SearchGroup.Content,
            Icons.Material.Filled.Videocam, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Partner | SearchTraits.Assistant,
            null, "ueberwachung"),
        new(nameof(TaskforceMessage), "Taskforce-Nachricht", "Taskforce-Chat", SearchGroup.Content,
            Icons.Material.Filled.Forum, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Partner | SearchTraits.Assistant,
            null, "chat"),
        // no detail page of their own: the provider stamps the list route on the hit
        new(nameof(Absence), "Abmeldung", "Abmeldungen", SearchGroup.Records,
            Icons.Material.Filled.EventBusy, SearchHitShape.Personal,
            SearchTraits.Assistant, null),
        new(nameof(Feedback), "Feedback", "Feedback-Meldungen", SearchGroup.Records,
            Icons.Material.Filled.Feedback, SearchHitShape.Personal,
            SearchTraits.Assistant, null),
        new(nameof(LibraryFile), "Bibliotheks-Datei", "Bibliotheks-Dateien", SearchGroup.Content,
            Icons.Material.Filled.FolderZip, SearchHitShape.Record,
            SearchTraits.Assistant, null),
        new(nameof(Followup), "Wiedervorlage", "Wiedervorlagen", SearchGroup.Content,
            Icons.Material.Filled.Alarm, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Partner | SearchTraits.Assistant,
            null, "wiedervorlagen"),
        new(nameof(Link), "Verknüpfung", "Verknüpfungen", SearchGroup.Content,
            Icons.Material.Filled.Link, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Partner | SearchTraits.Assistant,
            null, "verknuepfungen"),
        new(nameof(CustomFieldValue), "Eigenes Feld", "Eigene Felder", SearchGroup.Content,
            Icons.Material.Filled.Tune, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Partner | SearchTraits.Assistant,
            null, "zusatzfelder"),

        // ---- Persönliches: never anybody else's rows ----
        new(nameof(NooseiConversation), "NOOSEI-Unterhaltung", "NOOSEI-Unterhaltungen", SearchGroup.Personal,
            Icons.Material.Filled.SmartToy, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.Assistant, null),
        new(nameof(Notification), "Benachrichtigung", "Benachrichtigungen", SearchGroup.Personal,
            Icons.Material.Filled.Notifications, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.Assistant, null),
        new(nameof(SavedSearch), "Gespeicherte Suche", "Gespeicherte Suchen", SearchGroup.Personal,
            Icons.Material.Filled.BookmarkAdded, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.Assistant, null),
        new(nameof(GraphCanvasLayout), "Graph-Ansicht", "Graph-Ansichten", SearchGroup.Personal,
            Icons.Material.Filled.AccountTree, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.Assistant, null),
        new(nameof(WatchlistEntry), "Beobachtung", "Beobachtungsliste", SearchGroup.Personal,
            Icons.Material.Filled.Star, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.ContentChild | SearchTraits.Assistant,
            null, null),
        new(nameof(TextSnippet), "Textbaustein", "Textbausteine", SearchGroup.Personal,
            Icons.Material.Filled.TextSnippet, SearchHitShape.Personal,
            SearchTraits.Personal | SearchTraits.Assistant, null),

        new(nameof(Bewerbung), "Bewerbung", "Bewerbungen", SearchGroup.Records,
            Icons.Material.Filled.HowToReg, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant,
            "/bewerbungen/{0}"),
        new(nameof(BewerbungMessage), "Bewerbungs-Nachricht", "Bewerbungs-Nachrichten", SearchGroup.Content,
            Icons.Material.Filled.Mail, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "nachrichten"),
        new(nameof(Request), "Antrag", "Anträge", SearchGroup.Records,
            Icons.Material.Filled.RuleFolder, SearchHitShape.Record,
            SearchTraits.Assistant, null),
        new(nameof(SituationReport), "Lagebericht", "Lageberichte", SearchGroup.Records,
            Icons.Material.Filled.Summarize, SearchHitShape.Record,
            SearchTraits.Assistant, null),

        // ---- Verwaltung: catalogues, templates and rules ----
        new(nameof(Tag), "Stichwort", "Stichworte", SearchGroup.Administration,
            Icons.Material.Filled.Label, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(TrainingModule), "Ausbildungsmodul", "Ausbildungsmodule", SearchGroup.Administration,
            Icons.Material.Filled.School, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(FinancingItem), "Finanzierungs-Position", "Finanzierungs-Katalog", SearchGroup.Administration,
            Icons.Material.Filled.ShoppingCart, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(CounterIntelRule), "Gegenaufklärungs-Regel", "Gegenaufklärungs-Regeln", SearchGroup.Administration,
            Icons.Material.Filled.Policy, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(DocumentTemplate), "Dokument-Vorlage", "Dokument-Vorlagen", SearchGroup.Administration,
            Icons.Material.Filled.Article, SearchHitShape.Record, SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(ActivityTemplate), "Aktivitäts-Vorlage", "Aktivitäts-Vorlagen", SearchGroup.Administration,
            Icons.Material.Filled.PostAdd, SearchHitShape.Record, SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(PersonnelTemplate), "Personal-Vorlage", "Personal-Vorlagen", SearchGroup.Administration,
            Icons.Material.Filled.ContentPaste, SearchHitShape.Record, SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(DocTemplate), "Dok-Vorlage", "Dok-Vorlagen", SearchGroup.Administration,
            Icons.Material.Filled.NoteAdd, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(KassenBuchungVorlage), "Kassen-Vorlage", "Kassen-Vorlagen", SearchGroup.Administration,
            Icons.Material.Filled.Receipt, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(BewerbungTest), "Bewerbungs-Test", "Bewerbungs-Tests", SearchGroup.Administration,
            Icons.Material.Filled.Quiz, SearchHitShape.Record, SearchTraits.Assistant, null),
        new(nameof(Bewerbungssperre), "Bewerbungssperre", "Bewerbungssperren", SearchGroup.Administration,
            Icons.Material.Filled.Block, SearchHitShape.Record, SearchTraits.Assistant, null),

        // ---- Protokolle ----
        new(nameof(AuditLog), "Änderung", "Änderungsprotokoll", SearchGroup.Logs,
            Icons.Material.Filled.History, SearchHitShape.Log, SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(AccessLog), "Zugriff", "Zugriffsprotokoll", SearchGroup.Logs,
            Icons.Material.Filled.RemoveRedEye, SearchHitShape.Log, SearchTraits.Assistant, null),
        new(nameof(LlmRequestLog), "NOOSEI-Anfrage", "NOOSEI-Anfragen", SearchGroup.Logs,
            Icons.Material.Filled.QuestionAnswer, SearchHitShape.Log, SearchTraits.Heavy | SearchTraits.Assistant, null),

        // ---- Oeffentlicher Bereich ----
        // Appended, never interleaved: the array order is the facet-bar order AND the order of every NOOSEI schema
        // enum, which is the cached prompt prefix.
        // the notice has no page of its own: a hit targets the file it was published from
        new(nameof(OeffentlicheFahndung), "Öffentliche Ausschreibung", "Öffentliche Ausschreibungen", SearchGroup.Content,
            Icons.Material.Filled.PersonSearch, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "oeffentlich"),
        new(nameof(OeffentlichesFraktionsprofil), "Öffentliches Fraktionsprofil", "Öffentliche Fraktionsprofile",
            SearchGroup.Content, Icons.Material.Filled.Public, SearchHitShape.Content,
            SearchTraits.ContentChild | SearchTraits.Heavy | SearchTraits.Assistant,
            null, "oeffentlich"),
        new(nameof(Hinweis), "Bürgerhinweis", "Bürgerhinweise", SearchGroup.Records,
            Icons.Material.Filled.TipsAndUpdates, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant,
            "/hinweise/{0}"),
        // not Heavy: the subject is a short column, and the longtext lives on the message rows, which stay out
        new(nameof(Ticket), "Bürger-Ticket", "Bürger-Tickets", SearchGroup.Records,
            Icons.Material.Filled.Forum, SearchHitShape.Record, SearchTraits.Assistant,
            "/tickets/{0}"),
        // decided at a section of /fahndung, so the provider stamps the href
        new(nameof(FahndungEinspruch), "Einspruch", "Einsprüche", SearchGroup.Records,
            Icons.Material.Filled.Balance, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(Pressemitteilung), "Pressemitteilung", "Pressemitteilungen", SearchGroup.Administration,
            Icons.Material.Filled.Feed, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(OeffentlicheSeite), "Infoseite", "Infoseiten", SearchGroup.Administration,
            Icons.Material.Filled.MenuBook, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant, null),
        new(nameof(OeffentlicheWarnung), "Öffentliche Warnung", "Öffentliche Warnungen", SearchGroup.Administration,
            Icons.Material.Filled.Campaign, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant, null),
        // the singular differs from SituationReport's on purpose: two categories may not share a German name,
        // and NooseiRecordTypes keys a dictionary on it
        new(nameof(OeffentlicherLagebericht), "Öffentlicher Lagebericht", "Öffentliche Lageberichte",
            SearchGroup.Administration, Icons.Material.Filled.Assessment, SearchHitShape.Record,
            SearchTraits.Heavy | SearchTraits.Assistant, null),
        // the route carries the SLUG, not the id: /handbuch/{Slug} is the article address, and an id there 404s
        new(nameof(HandbookArticle), "Handbuch-Artikel", "Handbuch-Artikel", SearchGroup.Content,
            Icons.Material.Filled.MenuBook, SearchHitShape.Record,
            SearchTraits.Quick | SearchTraits.SideIndexed | SearchTraits.Assistant,
            "/handbuch/{0}"),
        // the glossary is a section of /handbuch, not a page: the term is opened by id through a query parameter,
        // because the route template is formatted, never URL-encoded, and a term carries spaces and umlauts
        new(nameof(GlossaryTerm), "Glossar-Begriff", "Glossar-Begriffe", SearchGroup.Content,
            Icons.Material.Filled.Abc, SearchHitShape.Record,
            SearchTraits.Quick | SearchTraits.SideIndexed | SearchTraits.Assistant,
            "/handbuch?begriff={0}"),
        // no SideIndexed: the phonetic index would cost a backfill version bump for a table of dozens of rows
        // whose whole content is a number and a short label. Like a glossary term it has no page of its own.
        new(nameof(RadioChannel), "Funkkanal", "Funkkanäle", SearchGroup.Content,
            Icons.Material.Filled.SettingsInputAntenna, SearchHitShape.Record,
            SearchTraits.Quick | SearchTraits.Assistant,
            "/funk?kanal={0}"),
    ];

    /// <summary>Entities deliberately left out of the search, each with the reason.</summary>
    /// <remarks>
    /// The coverage test reflects over every <c>DbSet</c> and fails unless the entity has a provider, is reachable
    /// as the child of one, or appears here. That is what keeps "everything the viewer may see" true as the schema
    /// grows, instead of true only on the day it was written.
    /// </remarks>
    public static readonly IReadOnlyDictionary<string, string> NotSearchable =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // --- no text of their own ---
            ["PersonPhoto"] = "Binärdatei ohne Text.",
            ["TextImage"] = "Binärdatei ohne Text; der Text, der sie einbettet, ist selbst durchsuchbar.",
            ["FactionPhoto"] = "Binärdatei ohne Text.",
            ["PersonAlias"] = "Match-Feld der Personenakte, keine eigene Kategorie.",
            ["PersonPhone"] = "Deep-Scan-Match-Feld der Personenakte.",
            ["PersonVehicle"] = "Deep-Scan-Match-Feld der Personenakte.",
            ["PersonLocation"] = "Deep-Scan-Match-Feld der Personenakte.",
            ["PersonWeapon"] = "Deep-Scan-Match-Feld der Personenakte.",
            ["FinancingRequestLine"] = "Position ohne eigenen Text; der Antrag trägt ihn.",
            ["EvidenceEntryLine"] = "Position ohne eigenen Text; das Asservat trägt ihn.",
            ["FactionRank"] = "Bezeichnung im Stammdaten-Block der Fraktion.",

            // --- id pairs: surfacing one is an assignment oracle over a restricted parent ---
            ["TagMapping"] = "Zuordnungszeile; über den Tag-Filter abgedeckt.",
            ["AgentActivityLink"] = "Verknüpfung Aktivität↔Organisation ohne eigenen Text.",
            ["FactionAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["PersonGroupAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["PartyAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["OperationAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["CaseAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["TaskforceAgent"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["JobAssignment"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel über eingeschränkte Aufgaben.",
            ["AppointmentAssignment"] = "Zuteilung ohne Text; wäre ein Zuteilungs-Orakel.",
            ["AnnouncementAcknowledgment"] = "Quittierung ohne Text.",
            ["MeetingSignOff"] = "Kein Freitext; wäre ein Anwesenheits-Orakel über Agenten.",
            ["MeetingAttendance"] = "Kein Freitext; wäre ein Anwesenheits-Orakel über Agenten.",
            ["AgentModuleCompletion"] = "Kein eigener Text; das Modul trägt ihn.",
            ["AgentBadge"] = "Auszeichnung ohne Freitext.",
            ["FactionMember"] = "Mitgliedschaft; die Person und die Fraktion sind je eigene Kategorien.",
            ["PersonGroupPhoto"] = "Binärdatei ohne Text.",
            ["PartyPhoto"] = "Binärdatei ohne Text.",
            ["PersonGroupMember"] = "Mitgliedschaft; die Person und die Gruppe sind je eigene Kategorien.",
            ["PartyMember"] = "Mitgliedschaft; die Person und die Partei sind je eigene Kategorien.",
            ["PersonRelation"] = "Notiz zwischen zwei Personen; beide Akten sind je eigene Kategorien.",
            ["AbductionCompromise"] = "Notiz an der Entführungsakte, die selbst durchsuchbar ist.",
            ["FactionInventory"] = "Bestandszeile; die Fraktionsakte trägt den Text.",
            ["FactionWeaponStock"] = "Bestandszeile; die Fraktionsakte trägt den Text.",
            ["FactionDrugRoute"] = "Bestandszeile; die Fraktionsakte trägt den Text.",
            ["ClassificationHistory"] = "Einstufungs-Verlauf; die Akte selbst ist durchsuchbar.",
            ["AgentRankHistory"] = "Dienstgrad-Verlauf; die Personalakte ist durchsuchbar.",
            ["AgentPromotionRequest"] = "Beförderungsantrag; die Personalakte ist durchsuchbar.",

            // --- numeric or derived state ---
            ["CaseNumberCounter"] = "Interner Aktenzeichen-Zähler.",
            ["ThreatScoreHistory"] = "Abgeleiteter Zahlenwert.",
            ["ThreatScoreConfig"] = "Reine Zahlen-Konfiguration.",
            ["RecencyThreshold"] = "Reine Zahlen-Konfiguration.",
            ["FinancingBudgetPeriod"] = "Abgeleiteter Kontostand.",
            ["LlmQuotaPeriod"] = "Abgeleiteter Kontingentstand.",
            ["LlmQuotaAdjustment"] = "Begründung einer Kontingent-Korrektur; nur der KI-Eigner darf sie lesen.",
            ["LeadDismissal"] = "Abgeleiteter Zustand des Hinweis-Feeds.",
            ["EnumLabelOverride"] = "Bezeichnungs-Konfiguration ohne Aktenbezug.",
            ["ProfileSuggestion"] = "Aggregiert aus Aktendaten inklusive eingestufter; kein Pro-Akte-Gate.",
            ["DossierSummary"] = "KI-Kurzbrief auf Mindestprivileg; die Akte selbst ist der Suchgegenstand.",

            // --- the index itself ---
            ["SearchPhoneticKey"] = "Der Suchindex selbst; ein Treffer darauf lieferte jede Akte doppelt und ungefiltert.",
            ["SearchStemToken"] = "Der Suchindex selbst; ein Treffer darauf lieferte jede Akte doppelt und ungefiltert.",

            // --- credentials, secrets and access-control rows ---
            ["SystemSetting"] = "Key/Value-Store mit Discord-Webhooks und Tokens; kein betrachter-skopierter Lesepfad.",
            ["AgentInvite"] = "Einziges Textfeld ist der Einladungs-Token, also ein Credential.",
            ["PartnerShare"] = "Freigabe-Zeile; sie zu listen wäre ein Zugriffs-Orakel.",
            ["DocumentAccessExclusion"] = "Entzugs-Zeile; sie zu listen wäre ein Zugriffs-Orakel.",
            ["CustomFieldDefinition"] = "Feld-Definition; die Werte sind durchsuchbar, die Definition ist Konfiguration.",

            // --- graded material and stale replays ---
            ["BewerbungTestQuestion"] = "Lösungsschlüssel.",
            ["BewerbungTestOption"] = "Lösungsschlüssel.",
            ["BewerbungTestAssignment"] = "Zuteilung ohne Text.",
            ["BewerbungTestAnswer"] = "Bewertetes Material.",
            ["NooseiMessage"] = "Der Rechte-Stempel macht gespeicherte Werkzeug-Antworten gegenüber dem aktuellen "
                + "Scope potenziell veraltet; nur der Titel der Unterhaltung ist durchsuchbar.",

            // --- public area ---
            ["BuergerProfil"] = "Konto des öffentlichen Bereichs, keine Akte. Der Name ist eine Fremdeingabe ohne "
                + "Aktenbezug und wird ausschließlich über den Bürger-Bestand in /einstellungen gesucht.",
            ["OeffentlichesModul"] = "Ein-/Aus-Schalter einer öffentlichen Seite, kein Inhalt. Der Katalog steht im "
                + "Code, bedient wird er in /einstellungen.",
            ["Warnhinweis"] = "Werteliste des öffentlichen Bereichs ohne Aktenbezug; gepflegt in /einstellungen. "
                + "Gesucht wird die Ausschreibung, die den Hinweis trägt, nicht der Hinweis selbst.",
            ["FahndungWarnhinweis"] = "Zuordnungszeile zwischen Ausschreibung und Warnhinweis; sie trägt keinen "
                + "eigenen Text.",
            ["FahndungKopfgeldAnteil"] = "Geldposten an einer Ausschreibung ohne eigenen Text; gefunden wird die "
                + "Akte, gepflegt wird der Anteil an ihrem Fahndungs-Panel. Eine durchsuchbare Geldliste wäre "
                + "obendrein ein Verzeichnis, welcher Agent privat auf wen gesetzt hat.",
            ["HinweisNachricht"] = "Schriftwechsel zu einem Hinweis; gefunden wird der Hinweis, nicht die "
                + "einzelne Zeile. Die interne Zielgruppe wäre über eine Volltextsuche sonst an der "
                + "Bearbeiter-Prüfung vorbei lesbar.",
            ["HinweisBelohnung"] = "Auszahlung an einen Hinweisgeber; gesucht wird der Hinweis oder die "
                + "Kassenbuchung. Eine durchsuchbare Geldliste müsste zusätzlich die Anonymitätszusage tragen.",
            ["TicketNachricht"] = "Schriftwechsel eines Tickets; gefunden wird das Ticket, nicht die "
                + "einzelne Zeile. Die interne Zielgruppe wäre über eine Volltextsuche sonst am "
                + "Führungs-Gate vorbei lesbar.",
            ["TicketParticipant"] = "Zuordnung eines Agenten zu einem Ticket, ohne eigenen Text. Gesucht wird "
                + "das Ticket; eine durchsuchbare Beteiligtenliste wäre eine Karte, wer woran sitzt.",
            ["OeffentlichesFuehrungsprofil"] = "Redaktioneller Eintrag des öffentlichen Organigramms, gepflegt "
                + "in /einstellungen. Eine interne Suche nach Führungskräften ist das Organigramm selbst.",
            ["OeffentlicheFaqRubrik"] = "Überschrift eines FAQ-Abschnitts ohne eigenen Text; die Fragen tragen ihn.",
            ["OeffentlicheFaqEintrag"] = "Redaktionelle Antwort für draußen; sie steht auf /faq und wird "
                + "über die öffentliche Suche gefunden, nicht über den Aktenbestand.",
            ["OeffentlicheVorlage"] = "Werteliste ohne Aktenbezug, gepflegt in /einstellungen. Gesucht wird "
                + "die Nachricht, die daraus entstand, nicht der Baustein.",
            ["HandbookChapter"] = "Überschrift eines Handbuch-Kapitels ohne eigenen Text; die Artikel tragen ihn.",
            ["LawBook"] = "Überschrift eines Gesetzbuchs ohne eigenen Text; gefunden werden seine Paragrafen.",
            ["HandbookStep"] = "Einzelner Schritt einer Anleitung; gefunden wird der Artikel, der ihn trägt.",
            ["ChangelogRelease"] = "Überschrift einer Fassung ohne eigenen Text; die Einträge tragen ihn.",
            ["ChangelogEntry"] = "Zeile über die Anwendung selbst, kein Aktenbestand. Sie steht vollständig "
                + "auf /neuerungen und wird dort gefiltert; in der Aktensuche wäre sie ein Treffer, der auf "
                + "keine Akte zeigt.",
        };

    private static readonly Dictionary<string, SearchCategory> ByClr =
        All.ToDictionary(c => c.Clr, StringComparer.Ordinal);

    private static readonly Dictionary<string, int> OrderByClr =
        All.Select((c, index) => (c.Clr, index)).ToDictionary(x => x.Clr, x => x.index, StringComparer.Ordinal);

    public static IReadOnlyList<SearchCategory> Categories => All;

    public static SearchCategory? Find(string? clr)
        => clr is not null && ByClr.TryGetValue(clr, out var category) ? category : null;

    /// <summary>Position in the catalog; an unknown category sorts last so a stray provider cannot reorder the bar.</summary>
    public static int Index(string clr) => OrderByClr.TryGetValue(clr, out var index) ? index : int.MaxValue;

    /// <summary>German singular. Never the CLR name — no English label reaches a result list.</summary>
    public static string German(string? clr) => Find(clr)?.German ?? "Eintrag";

    /// <summary>German plural, used as the heading of a result group.</summary>
    public static string Plural(string? clr) => Find(clr)?.Plural ?? German(clr);

    public static string Icon(string? clr) => Find(clr)?.Icon ?? Icons.Material.Filled.Article;

    public static SearchHitShape Shape(string? clr) => Find(clr)?.Shape ?? SearchHitShape.Record;

    public static bool Has(string? clr, SearchTraits trait) => Find(clr) is { } c && c.Has(trait);

    public static bool IsHeavy(string? clr) => Has(clr, SearchTraits.Heavy);

    public static IReadOnlyList<string> Clrs(SearchTraits trait)
        => All.Where(c => c.Has(trait)).Select(c => c.Clr).ToList();

    /// <summary>Href of a hit, or null when the category has no page of its own.</summary>
    public static string? Route(string? clr, string id)
        => Find(clr)?.RouteTemplate is { } template ? string.Format(template, id) : null;

    /// <summary>Whether a hit of this category leads to a page of its own.</summary>
    public static bool IsRoutable(string? clr) => Find(clr)?.RouteTemplate is not null;

    /// <summary>Section the content of this category lives in, appended to the parent route as <c>?tab=</c>.</summary>
    /// <remarks>A wrong slug is harmless — <c>RecordSectionRail</c> falls back to the first section — which is why
    /// a tab may be guessed while a record may not.</remarks>
    public static string? ParentTab(string? clr) => Find(clr)?.ParentTab;
}
