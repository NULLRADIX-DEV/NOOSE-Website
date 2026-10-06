using System.Security.Claims;
using MudBlazor;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Changelog;
using NOOSE_Website.Services.Handbook;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ITrashService" />
public sealed class TrashService(
    IPersonService people,
    IFactionService factions,
    IPersonGroupService groups,
    IPartyService parties,
    ICaseService cases,
    IOperationService operations,
    ITaskforceService taskforces,
    IJobService jobs,
    IAnnouncementService announcements,
    IAppointmentService appointments,
    IMeetingService meetings,
    IAgentActivityService activities,
    IAbsenceService absences,
    IAbductionService abductions,
    IInformantService informants,
    IRadioService radio,
    IEvidenceService evidence,
    IKassenService kasse,
    IFinancingService financing,
    IFeedbackService feedback,
    IPublicPageService publicPages,
    IPublicWantedService publicWanted,
    IPublicFactionProfileService factionProfiles,
    ITipService tips,
    ITicketService tickets,
    IObjectionService objections,
    IPressReleaseService press,
    IPublicWarningService warnings,
    IPublicReportService publicReports,
    IChangelogService changelog,
    IHandbookService handbook,
    ILawService laws) : ITrashService
{
    /// <summary>Loading and restoring for one record type; Restore stays a domain-service call.</summary>
    private sealed record TrashSource(
        TrashKind Kind,
        Func<CancellationToken, Task<List<TrashItem>>> Load,
        Func<string, ClaimsPrincipal, CancellationToken, Task> Restore);

    private static TrashSource Source<T>(
        TrashKind kind,
        Func<CancellationToken, Task<List<T>>> load,
        Func<T, TrashItem> project,
        Func<string, ClaimsPrincipal, CancellationToken, Task> restore)
        => new(kind, async ct => (await load(ct)).Select(project).ToList(), restore);

    private readonly IReadOnlyList<TrashSource> _sources =
    [
        Source(new TrashKind("personen", "Personen-Akten", Icons.Material.Filled.Badge, "/personen"),
            people.GetTrashAsync, TrashProjection.Person, people.RestoreAsync),
        Source(new TrashKind("fraktionen", "Fraktionen", Icons.Material.Filled.Groups, "/fraktionen"),
            factions.GetTrashAsync, TrashProjection.Faction, factions.RestoreAsync),
        Source(new TrashKind("personengruppen", "Personengruppen", Icons.Material.Filled.Diversity3, "/personengruppen"),
            groups.GetTrashAsync, TrashProjection.Group, groups.RestoreAsync),
        Source(new TrashKind("parteien", "Parteien", Icons.Material.Filled.AccountBalance, "/parteien"),
            parties.GetTrashAsync, TrashProjection.Party, parties.RestoreAsync),
        Source(new TrashKind("vorgaenge", "Vorgänge", Icons.Material.Filled.FolderSpecial, "/vorgaenge"),
            cases.GetTrashAsync, TrashProjection.Case, cases.RestoreAsync),
        Source(new TrashKind("operationen", "Operationen", Icons.Material.Filled.Radar, "/operationen"),
            operations.GetTrashAsync, TrashProjection.Operation, operations.RestoreAsync),
        Source(new TrashKind("taskforces", "Taskforces", Icons.Material.Filled.Groups2, "/taskforces"),
            taskforces.GetTrashAsync, TrashProjection.Taskforce, taskforces.RestoreAsync),
        Source(new TrashKind("aufgaben", "Aufgaben", Icons.Material.Filled.AssignmentTurnedIn, "/aufgaben"),
            jobs.GetTrashAsync, TrashProjection.Job, jobs.RestoreAsync),
        Source(new TrashKind("brett", "Ankündigungen", Icons.Material.Filled.Campaign, "/brett"),
            announcements.GetTrashAsync, TrashProjection.Announcement, announcements.RestoreAsync),
        Source(new TrashKind("kalender", "Termine", Icons.Material.Filled.CalendarMonth, "/kalender"),
            appointments.GetTrashAsync, TrashProjection.Appointment, appointments.RestoreAsync),
        Source(new TrashKind("besprechungen", "Besprechungen", Icons.Material.Filled.Groups, "/besprechungen"),
            meetings.GetTrashAsync, TrashProjection.Meeting, meetings.RestoreAsync),
        Source(new TrashKind("aktivitaeten", "Dienst-Aktivitäten", Icons.Material.Filled.Bolt, "/aktivitaeten"),
            activities.GetTrashAsync, TrashProjection.Activity, activities.RestoreAsync),
        Source(new TrashKind("abmeldungen", "Abmeldungen", Icons.Material.Filled.EventBusy, "/abmeldungen"),
            absences.GetTrashAsync, TrashProjection.Absence, absences.RestoreAsync),
        Source(new TrashKind("entfuehrungen", "Entführungen", Icons.Material.Filled.PersonOff, "/entfuehrungen"),
            abductions.GetTrashAsync, TrashProjection.Abduction, abductions.RestoreAsync),
        Source(new TrashKind("informanten", "Informanten", Icons.Material.Filled.VisibilityOff, "/informanten"),
            informants.GetTrashAsync, TrashProjection.Informant, informants.RestoreAsync),
        Source(new TrashKind("funk", "Funkkanäle", Icons.Material.Filled.SettingsInputAntenna, "/funk"),
            radio.GetTrashAsync, TrashProjection.RadioChannel, radio.RestoreAsync),
        Source(new TrashKind("asservate-items", "Asservate (Items)", Icons.Material.Filled.Inventory2, "/asservatenkammer"),
            evidence.GetItemTrashAsync, TrashProjection.EvidenceItem, evidence.RestoreItemAsync),
        Source(new TrashKind("asservate-eintraege", "Asservate (Einträge)", Icons.Material.Filled.ReceiptLong, "/asservatenkammer"),
            evidence.GetEntryTrashAsync, TrashProjection.EvidenceEntry, evidence.RestoreEntryAsync),
        Source(new TrashKind("kasse-buchungen", "Kassenbuchungen", Icons.Material.Filled.AccountBalanceWallet, "/kasse"),
            kasse.GetTrashAsync, TrashProjection.KassenBuchung, kasse.RestoreAsync),
        Source(new TrashKind("finanzierungen", "Finanzierungsanträge", Icons.Material.Filled.RequestQuote, "/finanzierungen"),
            financing.GetTrashAsync, TrashProjection.FinancingRequest, financing.RestoreAsync),
        Source(new TrashKind("feedback", "Feedback", Icons.Material.Filled.Feedback, "/feedback"),
            feedback.GetTrashAsync, TrashProjection.Feedback, feedback.RestoreAsync),
        Source(new TrashKind("oeffentliche-seiten", "Öffentliche Seiten", Icons.Material.Filled.MenuBook,
                "/einstellungen?tab=oeffentliche-seiten"),
            publicPages.GetTrashAsync, TrashProjection.PublicPage, publicPages.RestoreAsync),
        Source(new TrashKind("oeffentliche-fahndungen", "Öffentliche Ausschreibungen", Icons.Material.Filled.PersonSearch,
                "/fahndung?tab=oeffentlich"),
            publicWanted.GetTrashAsync, TrashProjection.PublicWanted, publicWanted.RestoreAsync),
        Source(new TrashKind("oeffentliche-fraktionsprofile", "Öffentliche Organisationsprofile",
                Icons.Material.Filled.Groups, "/fahndung?tab=organisationen"),
            factionProfiles.GetTrashAsync, TrashProjection.PublicFactionProfile, factionProfiles.RestoreAsync),
        Source(new TrashKind("hinweise", "Bürgerhinweise", Icons.Material.Filled.TipsAndUpdates, "/hinweise"),
            tips.GetTrashAsync, TrashProjection.Tip, tips.RestoreAsync),
        Source(new TrashKind("tickets", "Bürger-Tickets", Icons.Material.Filled.Forum, "/tickets"),
            tickets.GetTrashAsync, TrashProjection.Ticket, tickets.RestoreAsync),
        Source(new TrashKind("fahndungs-einsprueche", "Einsprüche", Icons.Material.Filled.Balance,
                "/fahndung?tab=einsprueche"),
            objections.GetTrashAsync, TrashProjection.Objection, objections.RestoreAsync),
        Source(new TrashKind("pressemitteilungen", "Pressemitteilungen", Icons.Material.Filled.Feed,
                "/einstellungen?tab=presse"),
            press.GetTrashAsync, TrashProjection.PressRelease, press.RestoreAsync),
        Source(new TrashKind("oeffentliche-warnungen", "Öffentliche Warnungen", Icons.Material.Filled.Campaign,
                "/einstellungen?tab=warnungen"),
            warnings.GetTrashAsync, TrashProjection.PublicWarning, warnings.RestoreAsync),
        Source(new TrashKind("oeffentliche-lageberichte", "Öffentliche Lageberichte",
                Icons.Material.Filled.Assessment, "/einstellungen?tab=berichte"),
            publicReports.GetTrashAsync, TrashProjection.PublicReport, publicReports.RestoreAsync),
        Source(new TrashKind("neuerungen", "Neuerungen", Icons.Material.Filled.NewReleases,
                "/einstellungen?tab=neuerungen"),
            changelog.GetTrashAsync, TrashProjection.ChangelogLine, changelog.RestoreAsync),
        Source(new TrashKind("handbuch-kapitel", "Handbuch-Kapitel", Icons.Material.Filled.MenuBook,
                "/handbuch"),
            handbook.GetChapterTrashAsync, TrashProjection.HandbookChapter, handbook.RestoreChapterAsync),
        Source(new TrashKind("handbuch-artikel", "Handbuch-Artikel", Icons.Material.Filled.Article,
                "/handbuch"),
            handbook.GetArticleTrashAsync, TrashProjection.HandbookArticle, handbook.RestoreArticleAsync),
        Source(new TrashKind("gesetzbuecher", "Gesetzbücher", Icons.Material.Filled.LibraryBooks, "/gesetze"),
            laws.GetBookTrashAsync, TrashProjection.LawBook, laws.RestoreBookAsync),
        Source(new TrashKind("gesetze", "Gesetze", Icons.Material.Filled.Gavel, "/gesetze"),
            laws.GetTrashAsync, TrashProjection.Law, laws.RestoreAsync),
    ];

    public IReadOnlyList<TrashKind> Kinds => _sources.Select(s => s.Kind).ToList();

    public async Task<List<TrashItem>> GetAsync(string kind, CancellationToken cancellationToken = default)
    {
        var rows = await Resolve(kind).Load(cancellationToken);
        return rows.OrderByDescending(r => r.DeletedAt).ToList();
    }

    public Task RestoreAsync(string kind, string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
        => Resolve(kind).Restore(id, actor, cancellationToken);

    private TrashSource Resolve(string kind)
        => _sources.FirstOrDefault(s => string.Equals(s.Kind.Key, kind, StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unbekannter Papierkorb-Typ.");
}
