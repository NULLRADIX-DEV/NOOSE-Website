using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Evidence;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Llm.Tools;
using NOOSE_Website.Services.Statistics;
using NSubstitute;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Builds the wide NOOSEI tools without spelling out every collaborator at each call site.</summary>
/// <remarks>Each of them fans out over one list service per record type, so the constructors grow with every type
/// that becomes enumerable. Named optional parameters keep a test to the services it actually asserts on, and a
/// new type adds one line here instead of breaking every test that ever built the tool.</remarks>
public static class NooseiToolHost
{
    public static FilterRecordsTool Filter(
        IPersonService? people = null,
        IFactionService? factions = null,
        IPersonGroupService? groups = null,
        IPartyService? parties = null,
        ICaseService? cases = null,
        IOperationService? operations = null,
        ILawService? laws = null,
        ITaskforceService? taskforces = null,
        IDocumentService? documents = null,
        IMeetingService? meetings = null,
        IJobService? jobs = null,
        IInformantService? informants = null,
        IEvidenceService? evidence = null,
        ILibraryService? library = null,
        IAbsenceService? absences = null,
        IAnnouncementService? announcements = null,
        IFinancingService? financing = null,
        IBewerbungService? applications = null,
        ISystemSettingService? settings = null,
        IAgentActivityService? activities = null,
        IAbductionService? abductions = null,
        ISituationReportService? situationReports = null,
        ITrainingModuleService? trainingModules = null,
        ICounterIntelRuleService? counterIntelRules = null,
        IFeedbackService? feedback = null,
        IDrugRouteService? drugRoutes = null)
        => new(
            people ?? Substitute.For<IPersonService>(),
            factions ?? Substitute.For<IFactionService>(),
            groups ?? Substitute.For<IPersonGroupService>(),
            parties ?? Substitute.For<IPartyService>(),
            drugRoutes ?? Substitute.For<IDrugRouteService>(),
            cases ?? Substitute.For<ICaseService>(),
            operations ?? Substitute.For<IOperationService>(),
            laws ?? Substitute.For<ILawService>(),
            taskforces ?? Substitute.For<ITaskforceService>(),
            documents ?? Substitute.For<IDocumentService>(),
            meetings ?? Substitute.For<IMeetingService>(),
            jobs ?? Substitute.For<IJobService>(),
            informants ?? Substitute.For<IInformantService>(),
            evidence ?? Substitute.For<IEvidenceService>(),
            library ?? Substitute.For<ILibraryService>(),
            absences ?? Substitute.For<IAbsenceService>(),
            announcements ?? Substitute.For<IAnnouncementService>(),
            financing ?? Substitute.For<IFinancingService>(),
            applications ?? Substitute.For<IBewerbungService>(),
            settings ?? Substitute.For<ISystemSettingService>(),
            activities ?? Substitute.For<IAgentActivityService>(),
            abductions ?? Substitute.For<IAbductionService>(),
            situationReports ?? Substitute.For<ISituationReportService>(),
            trainingModules ?? Substitute.For<ITrainingModuleService>(),
            counterIntelRules ?? Substitute.For<ICounterIntelRuleService>(),
            feedback ?? Substitute.For<IFeedbackService>());

    /// <summary>The area tool with every service the caller did not supply answering nothing.</summary>
    /// <remarks>Empty rather than a bare substitute: an unconfigured NSubstitute hands back a null list, which no
    /// real service ever does, and a tool written against that null would be guarding against a bug it cannot have.</remarks>
    public static ReadAreaTool Area(
        SqliteTestContext ctx,
        IKassenService? treasury = null,
        IEvidenceService? evidence = null,
        IAnnouncementService? announcements = null,
        ICounterIntelService? counterIntel = null,
        IFollowupService? followups = null,
        ITrainingModuleService? training = null,
        IPersonService? people = null,
        ISystemSettingService? settings = null,
        IDocumentTemplateService? documentTemplates = null,
        IActivityTemplateService? activityTemplates = null,
        IPersonnelTemplateService? personnelTemplates = null,
        IDocTemplateService? docTemplates = null,
        IKassenTemplateService? kassenTemplates = null,
        IFinancingCatalogService? financingCatalog = null,
        ITagService? tags = null,
        IBewerbungTestService? bewerbungTests = null,
        IBewerbungssperreService? bewerbungssperren = null,
        IBewerbungTemplateService? bewerbungTemplates = null,
        ITicketService? tickets = null,
        IPressReleaseService? press = null,
        IPublicPageService? publicPages = null,
        IPublicWarningService? publicWarnings = null,
        IPublicReportService? publicReports = null,
        IPublicSituationService? situation = null,
        INotificationService? notifications = null)
        => new(
            ctx.Factory,
            treasury ?? QuietTreasury(),
            evidence ?? QuietEvidence(),
            announcements ?? QuietBoard(),
            counterIntel ?? QuietCounterIntel(),
            followups ?? QuietFollowups(),
            training ?? QuietTraining(),
            people ?? QuietPeople(),
            settings ?? QuietSettings(),
            documentTemplates ?? Quiet<IDocumentTemplateService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<DocumentTemplate>()))),
            activityTemplates ?? Quiet<IActivityTemplateService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<ActivityTemplate>()))),
            personnelTemplates ?? Quiet<IPersonnelTemplateService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<PersonnelTemplate>()))),
            docTemplates ?? Quiet<IDocTemplateService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<DocTemplate>()))),
            kassenTemplates ?? Quiet<IKassenTemplateService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<KassenBuchungVorlage>()))),
            financingCatalog ?? Quiet<IFinancingCatalogService>(s =>
                s.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<FinancingItem>()))),
            tags ?? Quiet<ITagService>(s =>
                s.GetWithUsageAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new List<TagUsage>()))),
            bewerbungTests ?? Quiet<IBewerbungTestService>(s =>
                s.GetTestsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new List<BewerbungTest>()))),
            bewerbungssperren ?? Quiet<IBewerbungssperreService>(s =>
                s.ListActiveAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new List<BewerbungssperreInfo>()))),
            bewerbungTemplates ?? Quiet<IBewerbungTemplateService>(s =>
                s.ListAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new List<DocumentTemplate>()))),
            tickets ?? Quiet<ITicketService>(s =>
                s.GetInboxAsync(Arg.Any<TicketInboxScope>(), Arg.Any<string?>(), Arg.Any<bool>(),
                    Arg.Any<ClaimsPrincipal>(), Arg.Any<TicketKategorie?>(), Arg.Any<bool>(),
                    Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<TicketRow>>([]))),
            press ?? Quiet<IPressReleaseService>(s =>
                s.GetAllAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<PressEdit>>([]))),
            publicPages ?? Quiet<IPublicPageService>(s =>
                s.GetAllAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<PublicPageEdit>>([]))),
            publicWarnings ?? Quiet<IPublicWarningService>(s =>
                s.GetAllAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<WarningEdit>>([]))),
            publicReports ?? Quiet<IPublicReportService>(s =>
                s.GetAllAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<IReadOnlyList<PublicReportEdit>>([]))),
            situation ?? Quiet<IPublicSituationService>(s =>
                s.GetPublishedAsync(Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult<PublicSituationState?>(null))),
            notifications ?? Quiet<INotificationService>(s =>
                s.GetOwnAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
                    .Returns(Task.FromResult(new List<Notification>()))));

    private static IKassenService QuietTreasury()
    {
        var treasury = Substitute.For<IKassenService>();
        treasury.GetSummariesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KassenKontoSummary>>([]));
        treasury.GetLedgerAsync(Arg.Any<KassenKonto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<KassenBuchungDisplay>>([]));
        return treasury;
    }

    private static IEvidenceService QuietEvidence()
    {
        var evidence = Substitute.For<IEvidenceService>();
        evidence.GetItemsAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<EvidenceItemDisplay>()));
        return evidence;
    }

    private static IAnnouncementService QuietBoard()
    {
        var announcements = Substitute.For<IAnnouncementService>();
        announcements.GetBoardAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<AnnouncementRow>()));
        return announcements;
    }

    private static ICounterIntelService QuietCounterIntel()
    {
        var counterIntel = Substitute.For<ICounterIntelService>();
        counterIntel.GetOverviewAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new CounterIntelOverview(0, 0, 0, 0, 30)));
        counterIntel.GetFlagsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<InsiderFlag>>([]));
        return counterIntel;
    }

    private static IFollowupService QuietFollowups()
    {
        var followups = Substitute.For<IFollowupService>();
        followups.GetMyDueAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<FollowupDashboardItem>()));
        return followups;
    }

    private static ITrainingModuleService QuietTraining()
    {
        var training = Substitute.For<ITrainingModuleService>();
        training.GetActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<TrainingModule>()));
        return training;
    }

    private static IPersonService QuietPeople()
    {
        var people = Substitute.For<IPersonService>();
        people.GetListAsync(Arg.Any<ViewerScope>(), Arg.Any<ArchiveFilter>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new List<Person>()));
        return people;
    }

    private static ISystemSettingService QuietSettings()
    {
        var settings = Substitute.For<ISystemSettingService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult(new SystemConfiguration(
            false, null, null, BannerLevels.Info, null, null, null, null, null, false,
            HazardLevel.Critical, 5, 2, 3)));
        return settings;
    }

    /// <summary>A substitute configured by one lambda — for the many area services that need a single empty list.</summary>
    private static T Quiet<T>(Action<T> configure) where T : class
    {
        var sub = Substitute.For<T>();
        configure(sub);
        return sub;
    }
}
