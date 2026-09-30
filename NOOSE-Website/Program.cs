using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using MudBlazor.Services;
using NOOSE_Website.Authorization;
using NOOSE_Website.Components;
using NOOSE_Website.Components.Account;
using NOOSE_Website.Components.Factions;
using NOOSE_Website.Components.Groups;
using NOOSE_Website.Components.Parties;
using NOOSE_Website.Components.People;
using NOOSE_Website.Components.Common;
using NOOSE_Website.Components.Recruiting;
using NOOSE_Website.Components.Public;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.Authorization;
using NOOSE_Website.Infrastructure.Threat;
using NOOSE_Website.Infrastructure.Gamification;
using NOOSE_Website.Infrastructure.Search;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Infrastructure.Shares;
using NOOSE_Website.Infrastructure.Financing;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Infrastructure.Statistics;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Infrastructure.Followups;
using NOOSE_Website.Infrastructure.Tickets;
using NOOSE_Website.Infrastructure.Jobs;
using NOOSE_Website.Infrastructure.Meetings;
using NOOSE_Website.Infrastructure.Public;
using NOOSE_Website.Infrastructure.Recruiting;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Services.Search;
using NOOSE_Website.Services.Statistics;

var germanCulture = new CultureInfo("de-DE");
CultureInfo.DefaultThreadCurrentCulture = germanCulture;
CultureInfo.DefaultThreadCurrentUICulture = germanCulture;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddMudServices();

builder.Services.AddHttpContextAccessor();

// trust the loopback reverse proxy, plus the networks in ForwardedHeaders:KnownNetworks: on the server the app runs
// in its own container network and sees the host nginx as that network's gateway
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    foreach (var network in builder.Configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
    }
});

// persist data-protection keys to App_Data so a restart doesn't sign everyone out
var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys");
Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("NOOSE-Website");

builder.Services.Configure<FileUploadOptions>(builder.Configuration.GetSection("FileUpload"));

using var startupLoggerFactory = LoggerFactory.Create(lb =>
    lb.AddConfiguration(builder.Configuration.GetSection("Logging")).AddConsole());
var (connectionString, serverVersion) = DatabaseConnectionResolver.Resolve(
    builder.Configuration, startupLoggerFactory.CreateLogger("NOOSE.Datenbank"));

// exposes the circuit scope (for AuthenticationStateProvider) to app-wide singletons
builder.Services.AddCircuitServicesAccessor();
// Singleton so it fits the singleton-registered SaveChanges interceptors (see CurrentUserService)
builder.Services.AddSingleton<ICurrentUserService, CurrentUserService>();
// interceptors must be Singleton: resolved from the root provider of the singleton DbContext factory; per-context state lives in a ConditionalWeakTable
builder.Services.AddSingleton<ReadOnlyBarrierInterceptor>();
builder.Services.AddSingleton<RichTextHtmlInterceptor>();
builder.Services.AddSingleton<AuditSaveChangesInterceptor>();
builder.Services.AddSingleton<WatchlistChangeInterceptor>();
builder.Services.AddSingleton<SearchIndexInterceptor>();

// Singleton factory so created contexts don't hang off the circuit scope (avoids ObjectDisposedException on dialog/nav refresh)
builder.Services.AddDbContextFactory<AppDbContext>((sp, options) =>
    options.UseMySql(connectionString, serverVersion)
           .AddInterceptors(
               sp.GetRequiredService<ReadOnlyBarrierInterceptor>(),
               // before audit: the TextImage rows it adds still need their stamp
               sp.GetRequiredService<RichTextHtmlInterceptor>(),
               sp.GetRequiredService<AuditSaveChangesInterceptor>(),
               sp.GetRequiredService<WatchlistChangeInterceptor>(),
               sp.GetRequiredService<SearchIndexInterceptor>()) // last: rebuilds the search side-index from final state
           .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));

builder.Services.AddHealthChecks()
    .AddDbContextCheck<AppDbContext>("database");

// ---- Identity ----
builder.Services.AddIdentityCore<Agent>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.User.RequireUniqueEmail = false;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

builder.Services.AddScoped<IUserClaimsPrincipalFactory<Agent>, AgentClaimsPrincipalFactory>();

// kill switch: revalidate the security stamp every 30s
builder.Services.Configure<SecurityStampValidatorOptions>(options =>
    options.ValidationInterval = TimeSpan.FromSeconds(30));

// ---- Auth ----
var authentication = builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = IdentityConstants.ApplicationScheme;
    options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
});

// Discord is optional: only wired when client id+secret are configured
var discordClientId = builder.Configuration["Authentication:Discord:ClientId"];
var discordClientSecret = builder.Configuration["Authentication:Discord:ClientSecret"];
if (!string.IsNullOrWhiteSpace(discordClientId) && !string.IsNullOrWhiteSpace(discordClientSecret))
{
    authentication.AddDiscord(options =>
    {
        options.ClientId = discordClientId;
        options.ClientSecret = discordClientSecret;
        options.SignInScheme = IdentityConstants.ExternalScheme;
        options.Scope.Add("email");
        options.SaveTokens = true;
    });
}

authentication.AddIdentityCookies();

// ---- Authorization ----
builder.Services.AddNooseAuthorization();

// ---- Auth state ----
builder.Services.AddCascadingAuthenticationState();
// demo instance (Demo:AutoSetup): a constant demo principal the framework can never seed or
// revalidate away — a plain provider is not IHostEnvironmentAuthenticationStateProvider, so the
// circuit start can't push an anonymous state past CascadingAuthenticationState (which would
// dead-end on the disabled Discord login). Prod keeps the real revalidating provider.
if (builder.Configuration.GetValue<bool>("Demo:AutoSetup"))
{
    builder.Services.AddScoped<AuthenticationStateProvider, DemoAuthenticationStateProvider>();
}
else
{
    builder.Services.AddScoped<AuthenticationStateProvider, DemoAwareAuthenticationStateProvider>();
}

// ---- Services ----
builder.Services.AddScoped<IAgentManagementService, AgentManagementService>();
builder.Services.AddScoped<IAccessLogService, AccessLogService>();
builder.Services.AddScoped<IAuditLogQueryService, AuditLogQueryService>();
builder.Services.AddScoped<IFileStorageService, FileStorageService>();
builder.Services.AddScoped<ISourcesStorageService, SourcesStorageService>();
builder.Services.AddScoped<IFactionPhotoStorageService, FactionPhotoStorageService>();
builder.Services.AddScoped<IPersonGroupPhotoStorageService, PersonGroupPhotoStorageService>();
builder.Services.AddScoped<IPartyPhotoStorageService, PartyPhotoStorageService>();
builder.Services.AddScoped<IEvidenceImageStorageService, EvidenceImageStorageService>();
builder.Services.AddScoped<IAgentAvatarStorageService, AgentAvatarStorageService>();
builder.Services.AddScoped<IPublicWantedPhotoStorageService, PublicWantedPhotoStorageService>();
builder.Services.AddScoped<IPublicLeadershipPhotoStorageService, PublicLeadershipPhotoStorageService>();
builder.Services.AddScoped<ITipAttachmentStorageService, TipAttachmentStorageService>();
builder.Services.AddScoped<ITicketAttachmentStorageService, TicketAttachmentStorageService>();
builder.Services.AddScoped<ITicketAssistService, TicketAssistService>();
builder.Services.AddScoped<ITicketConversionService, TicketConversionService>();
builder.Services.AddScoped<ITextImageStorageService, TextImageStorageService>();
builder.Services.AddScoped<ICaseNumberService, CaseNumberService>();
builder.Services.AddScoped<IPersonService, PersonService>();
builder.Services.AddScoped<IPersonDocService, PersonDocService>();
builder.Services.AddScoped<IAbductionService, AbductionService>();
builder.Services.AddScoped<IEvidenceService, EvidenceService>();
builder.Services.AddScoped<IKassenService, KassenService>();
builder.Services.AddScoped<IKassenTemplateService, KassenTemplateService>();
builder.Services.AddScoped<IFinancingConfigService, FinancingConfigService>();
builder.Services.AddScoped<IFinancingCatalogService, FinancingCatalogService>();
builder.Services.AddScoped<IFinancingBudgetService, FinancingBudgetService>();
builder.Services.AddScoped<IFinancingService, FinancingService>();
builder.Services.AddScoped<IProfileSuggestionService, ProfileSuggestionService>();
builder.Services.AddScoped<IValueListLabelService, ValueListLabelService>();
builder.Services.AddScoped<IDocTemplateService, DocTemplateService>();
builder.Services.AddScoped<IDocumentService, DocumentService>();
builder.Services.AddScoped<IDocumentTemplateService, DocumentTemplateService>();
builder.Services.AddScoped<IDocumentAccessService, DocumentAccessService>();
builder.Services.AddScoped<IPlaceholderService, PlaceholderService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IRecencyService, RecencyService>();
builder.Services.AddScoped<IFollowupService, FollowupService>();
builder.Services.AddHostedService<FollowupDueWorker>();
builder.Services.AddHostedService<TicketFollowupWorker>();
builder.Services.AddHostedService<JobDueSoonWorker>();
builder.Services.AddScoped<ISourceService, SourceService>();
builder.Services.AddScoped<ITagService, TagService>();
builder.Services.AddScoped<ICommentService, CommentService>();
builder.Services.AddScoped<ITextImageService, TextImageService>();
builder.Services.AddScoped<ICustomFieldDefinitionService, CustomFieldDefinitionService>();
builder.Services.AddScoped<ICustomFieldValueService, CustomFieldValueService>();
builder.Services.AddScoped<ILinkService, LinkService>();
builder.Services.AddScoped<IRelationService, RelationService>();
builder.Services.AddScoped<IGraphService, GraphService>();
builder.Services.AddScoped<IGraphCanvasLayoutService, GraphCanvasLayoutService>();
builder.Services.AddScoped<ILinkSuggestionService, LinkSuggestionService>();
builder.Services.AddScoped<ITimelineService, TimelineService>();
builder.Services.AddScoped<IGlobalChronikService, GlobalChronikService>();
builder.Services.AddScoped<ILeadService, LeadService>();
builder.Services.AddScoped<ICounterIntelRuleService, CounterIntelRuleService>();
builder.Services.AddScoped<ICounterIntelService, CounterIntelService>();
builder.Services.AddScoped<IInformantService, InformantService>();

// AI assistant (OpenAI-compatible: OpenRouter or DeepSeek). Keys come from user-secrets / env, never the repo.
builder.Services.Configure<NOOSE_Website.Models.Llm.LlmOptions>(
    builder.Configuration.GetSection(NOOSE_Website.Models.Llm.LlmOptions.SectionName));
builder.Services.AddHttpClient("llm", (sp, client) =>
{
    var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<NOOSE_Website.Models.Llm.LlmOptions>>().Value;
    // No BaseAddress and no key here: the upstream is a runtime choice, and a pooled client that carried one
    // endpoint's credentials would hand them to the other. LlmService addresses and authorises each request.
    client.DefaultRequestHeaders.TryAddWithoutValidation("HTTP-Referer", "https://noose.info");
    client.DefaultRequestHeaders.TryAddWithoutValidation("X-Title", "NOOSE Intelligence");
    // Ceiling over all attempts; the per-attempt budget lives in LlmService.
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, o.TotalTimeoutSeconds));
});
builder.Services.AddScoped<ILlmService, LlmService>();
// Which upstream NOOSEI talks to, and the quota boost it earns — the AI owner's switch.
builder.Services.AddScoped<INooseiProviderService, NooseiProviderService>();
// NOOSEI token quota: the gateway is the only path to the transport, so nothing bypasses the meter.
builder.Services.AddScoped<ILlmQuotaConfigService, LlmQuotaConfigService>();
builder.Services.AddScoped<ILlmQuotaService, LlmQuotaService>();
builder.Services.AddScoped<INooseiGateway, NooseiGateway>();
builder.Services.AddScoped<INooseiSettingsService, NooseiSettingsService>();
builder.Services.AddScoped<IDossierSummaryService, DossierSummaryService>();
// NOOSEI record-database tools; the registry resolves whatever is registered here.
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.SearchRecordsTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.FilterRecordsTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.StatisticsTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ReadAreaTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ReadRecordTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ReadRecordContentTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ListRelatedTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.FindPathTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ReadTimelineTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.RecentChangesTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ResolveMentionTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.GetBriefTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ReadCalendarTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.ExplainThreatScoreTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.MyRecordsTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.INooseiTool, NOOSE_Website.Services.Llm.Tools.HandbookLookupTool>();
builder.Services.AddScoped<NOOSE_Website.Services.Llm.Tools.NooseiToolRegistry>();
builder.Services.AddScoped<INooseiChatService, NooseiChatService>();
builder.Services.AddScoped<ITextAssistService, TextAssistService>();
builder.Services.AddScoped<ILlmRequestLogService, LlmRequestLogService>();
builder.Services.AddScoped<ILlmAnomalyService, LlmAnomalyService>();
builder.Services.AddScoped<IOrgChartService, OrgChartService>();
builder.Services.AddScoped<IAppointmentService, AppointmentService>();
builder.Services.AddScoped<ICalendarService, CalendarService>();
builder.Services.AddScoped<IAbsenceService, AbsenceService>();
builder.Services.AddScoped<IFeedbackService, FeedbackService>();
builder.Services.AddScoped<IAttendanceStatisticsService, AttendanceStatisticsService>();
builder.Services.AddScoped<IMeetingService, MeetingService>();
builder.Services.AddHostedService<MeetingReminderWorker>();
builder.Services.AddScoped<IThreatScoreConfigService, ThreatScoreConfigService>();
builder.Services.AddScoped<IThreatScoreService, ThreatScoreService>();
builder.Services.AddScoped<IThreatTrendService, ThreatTrendService>();
builder.Services.AddHostedService<ThreatScoreSweepWorker>();
builder.Services.AddScoped<IGamificationService, GamificationService>();
builder.Services.AddHostedService<GamificationSweepWorker>();
builder.Services.AddScoped<ITopAgentAwardService, TopAgentAwardService>();
builder.Services.AddHostedService<TopAgentAwardWorker>();
// ---- global search: one provider per SearchCatalog category, coverage-tested against the catalog ----
builder.Services.Configure<SearchOptions>(builder.Configuration.GetSection(SearchOptions.SectionName));
builder.Services.AddScoped<ISearchService, SearchService>();
builder.Services.AddSearchProviders();
builder.Services.AddHostedService<SearchIndexBackfillWorker>();
builder.Services.AddScoped<ISavedSearchService, SavedSearchService>();
builder.Services.AddScoped<ITextSnippetService, TextSnippetService>();
builder.Services.AddScoped<IFactionService, FactionService>();
builder.Services.AddScoped<IPersonGroupService, PersonGroupService>();
builder.Services.AddScoped<IPartyService, PartyService>();
builder.Services.AddScoped<IOperationService, OperationService>();
builder.Services.AddScoped<IAgentActivityService, AgentActivityService>();
builder.Services.AddScoped<IActivityTemplateService, ActivityTemplateService>();
builder.Services.AddScoped<IPersonnelTemplateService, PersonnelTemplateService>();
builder.Services.AddScoped<ICaseService, CaseService>();
builder.Services.AddScoped<ITaskforceService, TaskforceService>();
builder.Services.AddScoped<ITaskforceChatService, TaskforceChatService>();
builder.Services.AddScoped<IMentionService, MentionService>();
builder.Services.AddSingleton<TaskforceChatBroadcaster>();
builder.Services.AddScoped<IObservationService, ObservationService>();
// fans the global recycle bin out over every record service, so restore keeps its guards
builder.Services.AddScoped<ITrashService, TrashService>();
builder.Services.AddScoped<IPersonnelFileService, PersonnelFileService>();
builder.Services.AddScoped<ITrainingModuleService, TrainingModuleService>();
builder.Services.AddScoped<IRequestService, RequestService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<IInventoryStatisticsService, InventoryStatisticsService>();
builder.Services.AddScoped<IThreatStatisticsService, ThreatStatisticsService>();
builder.Services.AddScoped<IActivityStatisticsService, ActivityStatisticsService>();
builder.Services.AddScoped<IThroughputStatisticsService, ThroughputStatisticsService>();
builder.Services.AddScoped<INetworkStatisticsService, NetworkStatisticsService>();
builder.Services.AddScoped<IWorkforceStatisticsService, WorkforceStatisticsService>();
builder.Services.AddScoped<IAbductionStatisticsService, AbductionStatisticsService>();
builder.Services.AddScoped<IKasseStatisticsService, KasseStatisticsService>();
builder.Services.AddScoped<IFinancingStatisticsService, FinancingStatisticsService>();
builder.Services.AddScoped<ISituationReportService, SituationReportService>();
builder.Services.AddHostedService<SituationReportWorker>();
builder.Services.AddHttpClient("discord", client => client.Timeout = TimeSpan.FromSeconds(5));
builder.Services.AddScoped<IDiscordWebhookService, DiscordWebhookService>();
builder.Services.AddScoped<INotificationService, NotificationService>();
builder.Services.AddSingleton<NotificationBroadcaster>();
builder.Services.AddSingleton<SharesBroadcaster>();
builder.Services.AddSingleton<DocumentAccessBroadcaster>();
builder.Services.AddSingleton<AcknowledgmentBroadcaster>();
builder.Services.AddSingleton<FinancingBroadcaster>();
builder.Services.AddScoped<IWatchlistService, WatchlistService>();
builder.Services.AddScoped<WatchlistFanout>();
builder.Services.AddSingleton<WatchlistDispatcher>();
builder.Services.AddScoped<IJobService, JobService>();
builder.Services.AddScoped<IAnnouncementService, AnnouncementService>();
builder.Services.AddScoped<ISystemSettingService, SystemSettingService>();
builder.Services.AddScoped<IDemoDataService, DemoDataService>();
builder.Services.AddScoped<INavPreferencesService, NavPreferencesService>();
builder.Services.AddScoped<INavLabelService, NavLabelService>();
builder.Services.AddScoped<IPartnerVisibilityPolicyService, PartnerVisibilityPolicyService>();
builder.Services.AddScoped<ILawService, LawService>();
builder.Services.AddScoped<IRadioService, RadioService>();
builder.Services.AddScoped<NOOSE_Website.Services.Changelog.IChangelogService,
    NOOSE_Website.Services.Changelog.ChangelogService>();
builder.Services.AddScoped<NOOSE_Website.Services.Handbook.IHandbookService,
    NOOSE_Website.Services.Handbook.HandbookService>();
builder.Services.AddScoped<ILibraryStorageService, LibraryStorageService>();
builder.Services.AddScoped<ILibraryService, LibraryService>();
builder.Services.AddScoped<IPersonMergeService, PersonMergeService>();
builder.Services.AddScoped<IPartnerShareService, PartnerShareService>();

// ---- recruiting (applications, invites, tests) ----
builder.Services.AddScoped<IAgentInviteService, AgentInviteService>();
builder.Services.AddScoped<IBewerbungService, BewerbungService>();
builder.Services.AddScoped<IApplicationCaseService, ApplicationCaseService>();
builder.Services.AddScoped<IRecruitingAutomationService, RecruitingAutomationService>();
builder.Services.AddScoped<ICareerRequirementsService, CareerRequirementsService>();
builder.Services.AddScoped<IBewerbungssperreService, BewerbungssperreService>();
builder.Services.AddScoped<IBewerbungTestService, BewerbungTestService>();
builder.Services.AddScoped<IBewerbungTestExpiryService, BewerbungTestExpiryService>();
builder.Services.AddHostedService<BewerbungTestExpiryWorker>();
builder.Services.AddScoped<IBewerbungTemplateService, BewerbungTemplateService>();

// ---- public area (citizen accounts, module switches) ----
builder.Services.AddScoped<IBuergerService, BuergerService>();
builder.Services.AddScoped<IPublicModuleService, PublicModuleService>();
builder.Services.AddScoped<IPublicPageService, PublicPageService>();
builder.Services.AddScoped<IPublicFaqService, PublicFaqService>();
builder.Services.AddScoped<IPressReleaseService, PressReleaseService>();
builder.Services.AddScoped<IPublicWarningService, PublicWarningService>();
builder.Services.AddScoped<IPublicLawService, PublicLawService>();
builder.Services.AddScoped<IPublicReportService, PublicReportService>();
builder.Services.AddScoped<IPublicSituationService, PublicSituationService>();
builder.Services.AddScoped<IPublicStatisticsService, PublicStatisticsService>();
builder.Services.AddScoped<IPublicSearchService, PublicSearchService>();
builder.Services.AddScoped<IPublicKpiService, PublicKpiService>();
builder.Services.AddScoped<IPublicLeadershipService, PublicLeadershipService>();
builder.Services.AddScoped<IPublicWantedService, PublicWantedService>();
builder.Services.AddHostedService<PublicWantedExpiryWorker>();
builder.Services.AddScoped<IWarnhinweisService, WarnhinweisService>();
builder.Services.AddScoped<IBountyService, BountyService>();
builder.Services.AddScoped<ITipPriorityService, TipPriorityService>();
builder.Services.AddScoped<ITipService, TipService>();
builder.Services.AddScoped<ITipTakeoverService, TipTakeoverService>();
builder.Services.AddScoped<IRewardService, RewardService>();
builder.Services.AddScoped<ITicketService, TicketService>();
builder.Services.AddScoped<IPublicTemplateService, PublicTemplateService>();
builder.Services.AddScoped<IPublicFactionProfileService, PublicFactionProfileService>();
builder.Services.AddScoped<IObjectionService, ObjectionService>();
builder.Services.AddSingleton<BewerbungBroadcaster>();
builder.Services.AddSingleton<TipsBroadcaster>();
builder.Services.AddSingleton<TicketBroadcaster>();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    // Answer with a body: a bodyless 429 is re-executed by UseStatusCodePagesWithReExecute, which would tell a
    // rate-limited visitor that the route does not exist.
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        context.HttpContext.Response.ContentType = "text/plain; charset=utf-8";
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString(NumberFormatInfo.InvariantInfo);
        }
        await context.HttpContext.Response.WriteAsync(
            "Zu viele Anfragen. Bitte versuche es in einer Minute erneut.", cancellationToken);
    };
    // Partitioned per caller: AddFixedWindowLimiter would share ONE bucket across the whole site, so ten
    // anonymous requests a minute could hold the login endpoint at 429 for every agent, citizen and applicant.
    options.AddPolicy(IdentityComponentsEndpointRouteBuilderExtensions.LoginRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            CallerKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // guards the tip attachment endpoint only. The submission itself travels over SignalR and never reaches this
    // middleware, so the real quota is the count in TipService.SubmitAsync
    options.AddPolicy(TipFileEndpointRouteBuilderExtensions.TipRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.GetAgentId() ?? CallerKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // same shape for the ticket attachments: the upload goes over SignalR, so this bounds the downloads only
    options.AddPolicy(TicketFileEndpointRouteBuilderExtensions.TicketRateLimitPolicy, context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.GetAgentId() ?? CallerKey(context),
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    // The public search rebuilds its haystacks per request, and it is a Razor route: it carries no endpoint
    // metadata a named policy could attach to, so it has to be gated here. Everything else - above all the
    // SignalR and framework paths - stays unlimited.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        context.Request.Path.StartsWithSegments("/suche-oeffentlich", StringComparison.OrdinalIgnoreCase)
            ? RateLimitPartition.GetFixedWindowLimiter(
                "suche:" + CallerKey(context),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 20,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                })
            : RateLimitPartition.GetNoLimiter<string>("unbegrenzt"));

    static string CallerKey(HttpContext context)
        => context.Connection.RemoteIpAddress?.ToString() ?? "unbekannt";
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // client retries for ~350s; outlive that so a network flap keeps unsaved input
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    })
    .AddHubOptions(options =>
    {
        // 25 MB: the RichTextEditor streams full HTML incl. base64 images over SignalR — do not lower
        options.MaximumReceiveMessageSize = 25 * 1024 * 1024;
    });

var app = builder.Build();

// forwarded headers must run first
app.UseForwardedHeaders();

app.UseRequestLocalization(new RequestLocalizationOptions()
    .SetDefaultCulture("de-DE")
    .AddSupportedCultures("de-DE")
    .AddSupportedUICultures("de-DE"));

// noindex for everything outside the public routes; before the error handler so re-executed pages keep the header
app.UseMiddleware<NOOSE_Website.Infrastructure.PublicIndexingMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseMiddleware<NOOSE_Website.Infrastructure.DemoModeMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();
// after Antiforgery on purpose: a tokenless POST must be refused before it spends a permit, or an anonymous
// visitor could hold the login endpoint at 429 for everyone
app.UseRateLimiter();

app.MapStaticAssets();
app.MapHealthChecks("/health").AllowAnonymous();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapNooseAccountEndpoints();
app.MapNoosePeopleFileEndpoints();
app.MapNooseAgentFileEndpoints();
app.MapNooseSourcesFileEndpoints();
app.MapNooseFactionsFileEndpoints();
app.MapNooseGroupsFileEndpoints();
app.MapNoosePartiesFileEndpoints();
app.MapNooseEvidenceFileEndpoints();
app.MapNooseLibraryFileEndpoints();
app.MapNooseSystemEndpoints();
app.MapNooseStatisticsExportEndpoints();
app.MapNooseRecruitingFileEndpoints();
app.MapNoosePublicWantedFileEndpoints();
app.MapNoosePublicLeadershipFileEndpoints();
app.MapNooseTipFileEndpoints();
app.MapNooseTicketFileEndpoints();
app.MapNooseTextImageFileEndpoints();

// apply pending migrations on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.MigrateAsync();

    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    if (!await roleManager.RoleExistsAsync("Admin"))
    {
        await roleManager.CreateAsync(new IdentityRole("Admin"));
    }

    // Content seeders write editorial material, and editorial material can collide with what a redaction has
    // already written - a unique slug or glossary name held by a row no seeder can see. Each one is isolated so a
    // collision costs its own content rather than the whole boot: an unhandled throw here aborts startup, systemd
    // restarts, and the site stays down until somebody edits the database by hand.
    async Task SeedAsync(string was, Func<Task> seed)
    {
        try
        {
            await seed();
        }
        catch (Exception ex)
        {
            app.Logger.LogError(ex, "Seeding {Bereich} fehlgeschlagen; der Start läuft ohne weiter.", was);
            // the failed rows are still tracked and the next seeder shares this context: it would retry them
            db.ChangeTracker.Clear();
        }
    }

    // seed the default recruiting message templates (idempotent)
    await SeedAsync("Bewerbungsvorlagen", () => NOOSE_Website.Infrastructure.RecruitingSeeder.SeedTemplatesAsync(db));

    // seed the auto-provisioned Sicherheitsüberprüfung case-document template (idempotent)
    await SeedAsync("Sicherheitsüberprüfung", () => NOOSE_Website.Infrastructure.ApplicationTemplateSeeder.SeedAsync(db));

    // seed one switch row per public module (idempotent; never overwrites a stored choice)
    await SeedAsync("Modulschalter", () => NOOSE_Website.Infrastructure.PublicModuleSeeder.SeedAsync(db));

    // seed the four editorial starter pages as drafts (idempotent; never overwrites an edited page)
    await SeedAsync("Öffentliche Seiten", () => NOOSE_Website.Infrastructure.PublicPageSeeder.SeedAsync(db));

    // seed the shipped changelog; keeps untouched lines current, never rewrites an edited one, revives nothing
    await SeedAsync("Neuerungen", () => NOOSE_Website.Infrastructure.Changelog.ChangelogSeeder.SeedAsync(db));

    // seed the shipped handbook; same promise as the changelog - an edited article is never rewritten
    await SeedAsync("Handbuch", () => NOOSE_Website.Infrastructure.Handbook.HandbookSeeder.SeedAsync(db));

    // seed the four starting warning chips (only while the table is empty; a deleted one stays deleted)
    await SeedAsync("Warnhinweise", () => NOOSE_Website.Infrastructure.WarnhinweisSeeder.SeedAsync(db));

    // seed one starting template per kind (only while the table is empty; a deleted one stays deleted)
    await SeedAsync("Bürger-Vorlagen", () => NOOSE_Website.Infrastructure.PublicTemplateSeeder.SeedAsync(db));

    // warm the static enum-label overrides so display classes show custom names
    var labelRows = await db.EnumLabelOverrides.Select(o => new { o.List, o.Key, o.Label }).ToListAsync();
    NOOSE_Website.Models.Enums.EnumLabelText.ReplaceAll(labelRows.Select(o => (o.List, o.Key, o.Label)));

    // demo instance only (Demo:AutoSetup): seed demo data + enable demo mode without a login
    await NOOSE_Website.Infrastructure.DemoAutoSetup.RunAsync(scope.ServiceProvider, builder.Configuration, app.Logger);
}

app.Run();
