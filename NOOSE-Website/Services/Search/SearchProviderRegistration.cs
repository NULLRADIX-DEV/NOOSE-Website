using NOOSE_Website.Services.Search.Providers;

namespace NOOSE_Website.Services.Search;

/// <summary>One provider per searchable <see cref="SearchCatalog"/> row.</summary>
/// <remarks>
/// Explicit rather than an assembly scan — the closest precedent in the composition root is the hand-listed tool
/// registry. A scan picks up whatever compiles, including a half-finished provider on a branch, which would then
/// quietly start answering agents. A missing line here is a failing coverage test, not a silently narrower search.
/// Order is not load-bearing: the orchestrator sorts by catalog position.
/// </remarks>
public static class SearchProviderRegistration
{
    public static IServiceCollection AddSearchProviders(this IServiceCollection services)
    {
        // Akten
        services.AddScoped<ISearchProvider, PersonSearchProvider>();
        services.AddScoped<ISearchProvider, FactionSearchProvider>();
        services.AddScoped<ISearchProvider, PersonGroupSearchProvider>();
        services.AddScoped<ISearchProvider, PartySearchProvider>();
        services.AddScoped<ISearchProvider, OperationSearchProvider>();
        services.AddScoped<ISearchProvider, CaseSearchProvider>();
        services.AddScoped<ISearchProvider, TaskforceSearchProvider>();
        services.AddScoped<ISearchProvider, JobSearchProvider>();
        services.AddScoped<ISearchProvider, LawSearchProvider>();

        // Betrieb
        services.AddScoped<ISearchProvider, AbductionSearchProvider>();
        services.AddScoped<ISearchProvider, EvidenceItemSearchProvider>();
        services.AddScoped<ISearchProvider, EvidenceEntrySearchProvider>();
        services.AddScoped<ISearchProvider, KassenBuchungSearchProvider>();
        services.AddScoped<ISearchProvider, FinancingRequestSearchProvider>();

        // Inhalte
        services.AddScoped<ISearchProvider, AgentActivitySearchProvider>();
        services.AddScoped<ISearchProvider, PersonDocSearchProvider>();
        services.AddScoped<ISearchProvider, SourceSearchProvider>();
        services.AddScoped<ISearchProvider, CommentSearchProvider>();

        // Wissen
        services.AddScoped<ISearchProvider, DocumentSearchProvider>();
        services.AddScoped<ISearchProvider, MeetingSearchProvider>();
        services.AddScoped<ISearchProvider, MeetingAgendaItemSearchProvider>();
        services.AddScoped<ISearchProvider, LibraryFileSearchProvider>();
        services.AddScoped<ISearchProvider, HandbookArticleSearchProvider>();
        services.AddScoped<ISearchProvider, GlossaryTermSearchProvider>();
        services.AddScoped<ISearchProvider, RadioChannelSearchProvider>();
        services.AddScoped<ISearchProvider, PresentationSearchProvider>();

        // Personal und Dienst
        services.AddScoped<ISearchProvider, AgentSearchProvider>();
        services.AddScoped<ISearchProvider, AgentNoteSearchProvider>();
        services.AddScoped<ISearchProvider, InformantSearchProvider>();
        services.AddScoped<ISearchProvider, InformantMeetingSearchProvider>();
        services.AddScoped<ISearchProvider, ObservationSearchProvider>();
        services.AddScoped<ISearchProvider, TaskforceMessageSearchProvider>();
        services.AddScoped<ISearchProvider, AnnouncementSearchProvider>();
        services.AddScoped<ISearchProvider, AppointmentSearchProvider>();
        services.AddScoped<ISearchProvider, AbsenceSearchProvider>();
        services.AddScoped<ISearchProvider, FeedbackSearchProvider>();

        // Querschnitt: agent-authored content that hangs off any record
        services.AddScoped<ISearchProvider, FollowupSearchProvider>();
        services.AddScoped<ISearchProvider, LinkSearchProvider>();
        services.AddScoped<ISearchProvider, CustomFieldValueSearchProvider>();

        // Persönliches
        services.AddScoped<ISearchProvider, NooseiConversationSearchProvider>();
        services.AddScoped<ISearchProvider, NotificationSearchProvider>();
        services.AddScoped<ISearchProvider, SavedSearchSearchProvider>();
        services.AddScoped<ISearchProvider, GraphCanvasLayoutSearchProvider>();
        services.AddScoped<ISearchProvider, WatchlistEntrySearchProvider>();
        services.AddScoped<ISearchProvider, TextSnippetSearchProvider>();

        // Verwaltung, Bewerbung und Protokolle
        services.AddScoped<ISearchProvider, RequestSearchProvider>();
        services.AddScoped<ISearchProvider, SituationReportSearchProvider>();
        services.AddScoped<ISearchProvider, BewerbungSearchProvider>();
        services.AddScoped<ISearchProvider, BewerbungMessageSearchProvider>();
        services.AddScoped<ISearchProvider, BewerbungssperreSearchProvider>();
        services.AddScoped<ISearchProvider, BewerbungTestSearchProvider>();
        services.AddScoped<ISearchProvider, TagSearchProvider>();
        services.AddScoped<ISearchProvider, TrainingModuleSearchProvider>();
        services.AddScoped<ISearchProvider, FinancingItemSearchProvider>();
        services.AddScoped<ISearchProvider, CounterIntelRuleSearchProvider>();
        services.AddScoped<ISearchProvider, DocumentTemplateSearchProvider>();
        services.AddScoped<ISearchProvider, ActivityTemplateSearchProvider>();
        services.AddScoped<ISearchProvider, PersonnelTemplateSearchProvider>();
        services.AddScoped<ISearchProvider, DocTemplateSearchProvider>();
        services.AddScoped<ISearchProvider, KassenTemplateSearchProvider>();
        services.AddScoped<ISearchProvider, AuditLogSearchProvider>();
        services.AddScoped<ISearchProvider, AccessLogSearchProvider>();
        services.AddScoped<ISearchProvider, LlmRequestLogSearchProvider>();

        // Oeffentlicher Bereich
        services.AddScoped<ISearchProvider, PublicWantedNoticeSearchProvider>();
        services.AddScoped<ISearchProvider, PublicFactionProfileSearchProvider>();
        services.AddScoped<ISearchProvider, TipSearchProvider>();
        services.AddScoped<ISearchProvider, TicketSearchProvider>();
        services.AddScoped<ISearchProvider, ObjectionSearchProvider>();
        services.AddScoped<ISearchProvider, PressReleaseSearchProvider>();
        services.AddScoped<ISearchProvider, PublicPageSearchProvider>();
        services.AddScoped<ISearchProvider, PublicWarningSearchProvider>();
        services.AddScoped<ISearchProvider, PublicReportSearchProvider>();

        return services;
    }
}
