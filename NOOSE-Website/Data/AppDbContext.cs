using System.Reflection;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Data.Entities.Announcements;
using NOOSE_Website.Data.Entities.Changelog;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Watchlist;
using NOOSE_Website.Data.Entities.Leads;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Feedback;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Abstractions;

namespace NOOSE_Website.Data;

/// <summary>Central EF-Core context.</summary>
public class AppDbContext : IdentityDbContext<Agent>
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();

    // person records
    public DbSet<Person> People => Set<Person>();
    public DbSet<PersonDoc> PersonDocs => Set<PersonDoc>();
    public DbSet<PersonPhoto> PersonPhotos => Set<PersonPhoto>();
    public DbSet<ClassificationHistory> ClassificationHistory => Set<ClassificationHistory>();
    public DbSet<PersonAlias> PersonAliases => Set<PersonAlias>();
    public DbSet<PersonPhone> PersonPhones => Set<PersonPhone>();
    public DbSet<PersonVehicle> PersonVehicles => Set<PersonVehicle>();
    public DbSet<PersonLocation> PersonLocations => Set<PersonLocation>();
    public DbSet<PersonWeapon> PersonWeapons => Set<PersonWeapon>();
    public DbSet<ProfileSuggestion> ProfileSuggestions => Set<ProfileSuggestion>();
    public DbSet<CaseNumberCounter> CaseNumberCounter => Set<CaseNumberCounter>();

    // ---- cross-cutting: tags, comments, sources ----
    public DbSet<Source> Sources => Set<Source>();

    /// <summary>Images pasted into plain-text fields, referenced from the text by a mention token.</summary>
    public DbSet<TextImage> TextImages => Set<TextImage>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<TagMapping> TagMappings => Set<TagMapping>();
    public DbSet<NOOSE_Website.Data.Entities.Search.SearchPhoneticKey> SearchPhoneticKeys => Set<NOOSE_Website.Data.Entities.Search.SearchPhoneticKey>();
    public DbSet<NOOSE_Website.Data.Entities.Search.SearchStemToken> SearchStemTokens => Set<NOOSE_Website.Data.Entities.Search.SearchStemToken>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<EnumLabelOverride> EnumLabelOverrides => Set<EnumLabelOverride>();

    // ---- links and relations ----
    public DbSet<Link> Links => Set<Link>();
    public DbSet<PersonRelation> PersonRelations => Set<PersonRelation>();

    public DbSet<SavedSearch> SavedSearch => Set<SavedSearch>();
    public DbSet<GraphCanvasLayout> GraphCanvasLayouts => Set<GraphCanvasLayout>();
    public DbSet<TextSnippet> Textbausteine => Set<TextSnippet>();

    // factions
    public DbSet<Faction> Factions => Set<Faction>();
    public DbSet<FactionRank> FactionRanks => Set<FactionRank>();
    public DbSet<FactionWeaponStock> FactionWeaponStocks => Set<FactionWeaponStock>();
    public DbSet<FactionInventory> FactionInventories => Set<FactionInventory>();
    public DbSet<FactionDrugRoute> FactionDrugRoutes => Set<FactionDrugRoute>();
    public DbSet<FactionMember> FactionMembers => Set<FactionMember>();
    public DbSet<FactionAgent> FactionAgents => Set<FactionAgent>();
    public DbSet<FactionPhoto> FactionPhotos => Set<FactionPhoto>();

    // person groups
    public DbSet<PersonGroup> PersonGroups => Set<PersonGroup>();
    public DbSet<PersonGroupMember> PersonGroupMembers => Set<PersonGroupMember>();
    public DbSet<PersonGroupAgent> PersonGroupAgents => Set<PersonGroupAgent>();
    public DbSet<PersonGroupPhoto> PersonGroupPhotos => Set<PersonGroupPhoto>();

    // parties
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<PartyMember> PartyMembers => Set<PartyMember>();
    public DbSet<PartyAgent> PartyAgents => Set<PartyAgent>();
    public DbSet<PartyPhoto> PartyPhotos => Set<PartyPhoto>();

    // operations
    public DbSet<Operation> Operations => Set<Operation>();
    public DbSet<OperationAgent> OperationAgents => Set<OperationAgent>();

    // agent activities + org links + templates
    public DbSet<AgentActivity> AgentActivities => Set<AgentActivity>();
    public DbSet<AgentActivityLink> AgentActivityLinks => Set<AgentActivityLink>();
    public DbSet<ActivityTemplate> ActivityTemplates => Set<ActivityTemplate>();
    public DbSet<PersonnelTemplate> PersonnelTemplates => Set<PersonnelTemplate>();

    // cases
    public DbSet<Case> Cases => Set<Case>();
    public DbSet<CaseAgent> CaseAgents => Set<CaseAgent>();

    // taskforces
    public DbSet<Taskforce> Taskforces => Set<Taskforce>();
    public DbSet<TaskforceAgent> TaskforceAgents => Set<TaskforceAgent>();
    public DbSet<TaskforceMessage> TaskforceMessages => Set<TaskforceMessage>();

    // ---- observations ----
    public DbSet<Observation> Observations => Set<Observation>();

    // ---- agent abductions + compromised records ----
    public DbSet<AgentAbduction> AgentAbductions => Set<AgentAbduction>();
    public DbSet<AbductionCompromise> AbductionCompromises => Set<AbductionCompromise>();

    // ---- evidence room (Asservatenkammer) ----
    public DbSet<EvidenceItem> EvidenceItems => Set<EvidenceItem>();
    public DbSet<EvidenceEntry> EvidenceEntries => Set<EvidenceEntry>();
    public DbSet<EvidenceEntryLine> EvidenceEntryLines => Set<EvidenceEntryLine>();

    // ---- Fraktions-Kasse (NOOSE treasury) ----
    public DbSet<KassenBuchung> KassenBuchungen => Set<KassenBuchung>();
    public DbSet<KassenBuchungVorlage> KassenVorlagen => Set<KassenBuchungVorlage>();

    // ---- funding requests (catalog, basket, monthly budget) ----
    public DbSet<FinancingItem> FinancingItems => Set<FinancingItem>();
    public DbSet<FinancingRequest> FinancingRequests => Set<FinancingRequest>();
    public DbSet<FinancingRequestLine> FinancingRequestLines => Set<FinancingRequestLine>();
    public DbSet<FinancingBudgetPeriod> FinancingBudgetPeriods => Set<FinancingBudgetPeriod>();

    // ---- NOOSEI token quota (request log, weekly ledger, manual corrections) ----
    public DbSet<LlmRequestLog> LlmRequests => Set<LlmRequestLog>();
    public DbSet<LlmQuotaPeriod> LlmQuotaPeriods => Set<LlmQuotaPeriod>();
    public DbSet<LlmQuotaAdjustment> LlmQuotaAdjustments => Set<LlmQuotaAdjustment>();
    public DbSet<NooseiConversation> NooseiConversations => Set<NooseiConversation>();
    public DbSet<NooseiMessage> NooseiMessages => Set<NooseiMessage>();

    // per-agent personnel file
    public DbSet<AgentRankHistory> AgentRankHistories => Set<AgentRankHistory>();
    public DbSet<AgentNote> AgentNotes => Set<AgentNote>();
    public DbSet<AgentPromotionRequest> AgentPromotionRequests => Set<AgentPromotionRequest>();

    // training modules + per-agent completion
    public DbSet<TrainingModule> TrainingModules => Set<TrainingModule>();
    public DbSet<AgentModuleCompletion> AgentModuleCompletions => Set<AgentModuleCompletion>();

    // request/inbox workflow
    public DbSet<Request> Requests => Set<Request>();

    // in-app notifications
    public DbSet<Notification> Notifications => Set<Notification>();

    // watchlist (followed records)
    public DbSet<WatchlistEntry> Watchlists => Set<WatchlistEntry>();
    public DbSet<LeadDismissal> LeadDismissals => Set<LeadDismissal>();
    public DbSet<Informant> Informants => Set<Informant>();
    public DbSet<InformantMeeting> InformantMeetings => Set<InformantMeeting>();

    // jobs/to-dos & assignments
    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<JobAssignment> JobAssignments => Set<JobAssignment>();

    // calendar appointments & participants
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<AppointmentAssignment> AppointmentAssignments => Set<AppointmentAssignment>();

    // agent sign-offs over whole days
    public DbSet<Absence> Absences => Set<Absence>();

    // agent feedback about the website itself
    public DbSet<Feedback> Feedbacks => Set<Feedback>();

    // meetings, agenda, per-meeting sign-offs & the frozen attendance roster
    public DbSet<Meeting> Meetings => Set<Meeting>();
    public DbSet<MeetingAgendaItem> MeetingAgendaItems => Set<MeetingAgendaItem>();
    public DbSet<MeetingSignOff> MeetingSignOffs => Set<MeetingSignOff>();
    public DbSet<MeetingAttendance> MeetingAttendances => Set<MeetingAttendance>();

    // ---- announcements ----
    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementAcknowledgment> AnnouncementAcknowledgments => Set<AnnouncementAcknowledgment>();

    // doc templates (admin-defined entry masks)
    public DbSet<DocTemplate> DocTemplates => Set<DocTemplate>();

    // leadership-defined counter-intelligence rules
    public DbSet<CounterIntelRule> CounterIntelRules => Set<CounterIntelRule>();

    // configurable custom fields per record type
    public DbSet<CustomFieldDefinition> CustomFieldDefinitions => Set<CustomFieldDefinition>();
    public DbSet<CustomFieldValue> CustomFieldValues => Set<CustomFieldValue>();

    // document library + document templates
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<DocumentTemplate> DocumentTemplates => Set<DocumentTemplate>();

    // ---- recency + followups ----
    public DbSet<RecencyThreshold> RecencyThresholds => Set<RecencyThreshold>();
    public DbSet<ThreatScoreConfig> ThreatScoreConfigs => Set<ThreatScoreConfig>();
    public DbSet<ThreatScoreHistory> ThreatScoreHistory => Set<ThreatScoreHistory>();
    public DbSet<NOOSE_Website.Data.Entities.Gamification.AgentBadge> AgentBadges => Set<NOOSE_Website.Data.Entities.Gamification.AgentBadge>();
    public DbSet<Followup> Followups => Set<Followup>();

    // cached AI dossier summaries (one current row per record)
    public DbSet<DossierSummary> DossierSummaries => Set<DossierSummary>();

    // archived monthly situation reports
    public DbSet<SituationReport> SituationReports => Set<SituationReport>();

    // system settings, law book, file library
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();
    public DbSet<Law> Laws => Set<Law>();
    public DbSet<LawBook> LawBooks => Set<LawBook>();
    public DbSet<LibraryFile> LibraryFiles => Set<LibraryFile>();

    // ---- partner record releases ----
    public DbSet<PartnerShare> PartnerShares => Set<PartnerShare>();
    public DbSet<DocumentAccessExclusion> DocumentAccessExclusions => Set<DocumentAccessExclusion>();

    // ---- recruiting (applications, invites, tests) ----
    public DbSet<AgentInvite> AgentInvites => Set<AgentInvite>();
    public DbSet<Bewerbung> Bewerbungen => Set<Bewerbung>();
    public DbSet<BewerbungMessage> BewerbungMessages => Set<BewerbungMessage>();
    public DbSet<BewerbungTest> BewerbungTests => Set<BewerbungTest>();
    public DbSet<BewerbungTestQuestion> BewerbungTestQuestions => Set<BewerbungTestQuestion>();
    public DbSet<BewerbungTestOption> BewerbungTestOptions => Set<BewerbungTestOption>();
    public DbSet<BewerbungTestAssignment> BewerbungTestAssignments => Set<BewerbungTestAssignment>();
    public DbSet<BewerbungTestAnswer> BewerbungTestAnswers => Set<BewerbungTestAnswer>();
    public DbSet<Bewerbungssperre> Bewerbungssperren => Set<Bewerbungssperre>();

    // ---- public area (citizen accounts) ----
    public DbSet<BuergerProfil> BuergerProfile => Set<BuergerProfil>();
    public DbSet<OeffentlichesModul> OeffentlicheModule => Set<OeffentlichesModul>();
    public DbSet<OeffentlicheSeite> OeffentlicheSeiten => Set<OeffentlicheSeite>();
    public DbSet<OeffentlicheFaqRubrik> OeffentlicheFaqRubriken => Set<OeffentlicheFaqRubrik>();
    public DbSet<OeffentlicheFaqEintrag> OeffentlicheFaqEintraege => Set<OeffentlicheFaqEintrag>();
    public DbSet<OeffentlicheFahndung> OeffentlicheFahndungen => Set<OeffentlicheFahndung>();
    public DbSet<Warnhinweis> Warnhinweise => Set<Warnhinweis>();
    public DbSet<FahndungWarnhinweis> FahndungWarnhinweise => Set<FahndungWarnhinweis>();
    public DbSet<FahndungKopfgeldAnteil> FahndungKopfgeldAnteile => Set<FahndungKopfgeldAnteil>();
    public DbSet<Hinweis> Hinweise => Set<Hinweis>();
    public DbSet<HinweisNachricht> HinweisNachrichten => Set<HinweisNachricht>();
    public DbSet<HinweisBelohnung> HinweisBelohnungen => Set<HinweisBelohnung>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
    public DbSet<TicketNachricht> TicketNachrichten => Set<TicketNachricht>();
    public DbSet<TicketParticipant> TicketBeteiligte => Set<TicketParticipant>();
    public DbSet<OeffentlichesFuehrungsprofil> OeffentlicheFuehrungsprofile => Set<OeffentlichesFuehrungsprofil>();
    public DbSet<OeffentlicheVorlage> OeffentlicheVorlagen => Set<OeffentlicheVorlage>();
    public DbSet<OeffentlichesFraktionsprofil> OeffentlicheFraktionsprofile => Set<OeffentlichesFraktionsprofil>();
    public DbSet<FahndungEinspruch> FahndungEinsprueche => Set<FahndungEinspruch>();
    public DbSet<Pressemitteilung> Pressemitteilungen => Set<Pressemitteilung>();
    public DbSet<OeffentlicheWarnung> OeffentlicheWarnungen => Set<OeffentlicheWarnung>();
    public DbSet<OeffentlicherLagebericht> OeffentlicheLageberichte => Set<OeffentlicherLagebericht>();
    public DbSet<ChangelogRelease> Aenderungsfassungen => Set<ChangelogRelease>();
    public DbSet<ChangelogEntry> Aenderungseintraege => Set<ChangelogEntry>();
    public DbSet<HandbookChapter> HandbuchKapitel => Set<HandbookChapter>();
    public DbSet<HandbookArticle> HandbuchArtikel => Set<HandbookArticle>();
    public DbSet<HandbookStep> HandbuchSchritte => Set<HandbookStep>();
    public DbSet<GlossaryTerm> HandbuchBegriffe => Set<GlossaryTerm>();
    public DbSet<RadioChannel> Funkkanaele => Set<RadioChannel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Agent>(b =>
        {
            b.Property(a => a.Codename).HasMaxLength(128);
            b.Property(a => a.RealName).HasMaxLength(128);
            b.Property(a => a.BadgeNumber).HasMaxLength(32);
            b.Property(a => a.DiscordId).HasMaxLength(32);
            b.Property(a => a.DiscordUsername).HasMaxLength(64);
            b.Property(a => a.AvatarUrl).HasMaxLength(512);
            b.Property(a => a.AvatarFileName).HasMaxLength(128);
            b.Property(a => a.AvatarContentType).HasMaxLength(100);
            b.Property(a => a.PendingAvatarFileName).HasMaxLength(128);
            b.Property(a => a.PendingAvatarContentType).HasMaxLength(100);
            b.Property(a => a.BlockedReason).HasMaxLength(512);
            b.Property(a => a.TerminatedById).HasMaxLength(64);
            b.Property(a => a.TerminatedByName).HasMaxLength(128);
            b.Property(a => a.TerminationReason).HasMaxLength(2000);
            // longtext for JSON
            b.Property(a => a.NavPreferencesJson).HasColumnType("longtext");
            b.Property(a => a.FinancingBudgetOverride).HasPrecision(18, 2);
            b.HasIndex(a => a.DiscordId).IsUnique();
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.Property(a => a.EntityType).HasMaxLength(128);
            b.Property(a => a.EntityId).HasMaxLength(64);
            b.Property(a => a.AgentId).HasMaxLength(64);
            b.Property(a => a.AgentName).HasMaxLength(128);
            b.HasIndex(a => new { a.EntityType, a.EntityId });
            b.HasIndex(a => a.Timestamp);
            // window scans: Leads / Chronik / Gamification
            b.HasIndex(a => new { a.EntityType, a.Timestamp });
        });

        modelBuilder.Entity<AccessLog>(b =>
        {
            b.Property(a => a.EntityType).HasMaxLength(128);
            b.Property(a => a.EntityId).HasMaxLength(64);
            b.Property(a => a.AgentId).HasMaxLength(64);
            b.Property(a => a.AgentName).HasMaxLength(128);
            b.HasIndex(a => new { a.EntityType, a.EntityId });
            // counter-intelligence cockpit: window + per-agent scans
            b.HasIndex(a => a.Timestamp);
            b.HasIndex(a => new { a.AgentId, a.Timestamp });
            // own views of one record: "new since your last visit"
            b.HasIndex(a => new { a.AgentId, a.EntityType, a.EntityId, a.Timestamp });
        });

        modelBuilder.Entity<ThreatScoreHistory>(b =>
        {
            b.Property(a => a.EntityType).HasMaxLength(128);
            b.Property(a => a.EntityId).HasMaxLength(64);
            b.Property(a => a.DetailJson).HasColumnType("longtext");
            b.HasIndex(a => new { a.EntityType, a.EntityId, a.Timestamp });
            // chronicle scans a plain time window across all records
            b.HasIndex(a => a.Timestamp);
        });

        modelBuilder.Entity<NOOSE_Website.Data.Entities.Gamification.AgentBadge>(b =>
        {
            b.Property(a => a.BadgeKey).HasMaxLength(64);
            b.HasIndex(a => new { a.AgentId, a.BadgeKey }).IsUnique();
            b.HasOne<Agent>().WithMany().HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DossierSummary>(b =>
        {
            b.Property(a => a.EntityType).HasMaxLength(128);
            b.Property(a => a.EntityId).HasMaxLength(64);
            b.Property(a => a.ContentHash).HasMaxLength(64);
            b.Property(a => a.Model).HasMaxLength(128);
            b.Property(a => a.BriefJson).HasColumnType("longtext");
            b.HasIndex(a => new { a.EntityType, a.EntityId }).IsUnique();
        });

        modelBuilder.Entity<GraphCanvasLayout>(b =>
        {
            b.Property(a => a.AgentId).HasMaxLength(64);
            b.Property(a => a.Name).HasMaxLength(160);
            b.Property(a => a.LayoutJson).HasColumnType("longtext");
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<LeadDismissal>(b =>
        {
            b.Property(a => a.LeadKey).HasMaxLength(256);
            b.HasIndex(a => a.LeadKey);
        });

        modelBuilder.Entity<Informant>(b =>
        {
            b.Property(i => i.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(i => i.RealName).HasMaxLength(256);
            b.Property(i => i.PersonId).HasMaxLength(64);
            b.Property(i => i.FactionId).HasMaxLength(64);
            b.Property(i => i.Description).HasColumnType("longtext");
            b.Property(i => i.ContactInfo).HasColumnType("longtext");
            b.Property(i => i.Notes).HasColumnType("longtext");
            b.Property(i => i.HandlerId).HasMaxLength(64);
            b.HasIndex(i => i.CaseNumber).IsUnique();
            b.HasIndex(i => i.HandlerId);
            // at most one informant per person record (MySQL allows repeated NULLs here)
            b.HasIndex(i => i.PersonId).IsUnique();
            // several informants may report on the same faction
            b.HasIndex(i => i.FactionId);
            // never cascade off the Agent table
            b.HasOne<Agent>().WithMany().HasForeignKey(i => i.HandlerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Person>().WithMany().HasForeignKey(i => i.PersonId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Faction>().WithMany().HasForeignKey(i => i.FactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(i => i.Meetings).WithOne()
                .HasForeignKey(m => m.InformantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<InformantMeeting>(b =>
        {
            b.Property(m => m.Location).HasMaxLength(256);
            b.Property(m => m.Content).HasColumnType("longtext");
            b.HasIndex(m => m.InformantId);
        });

        modelBuilder.Entity<Person>(b =>
        {
            b.Property(p => p.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.ThreatDetailJson).HasColumnType("longtext");
            b.Property(p => p.ArchiveReason).HasMaxLength(300);
            b.HasIndex(p => p.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(p => p.IsArchived);
            b.HasIndex(p => p.Name);
            b.HasIndex(p => p.IsClassified);
            // growth and capture series scan a date window over the whole table
            b.HasIndex(p => p.CreatedAt);

            b.HasMany(p => p.Aliases).WithOne(a => a.Person!)
                .HasForeignKey(a => a.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.PhoneNumbers).WithOne(t => t.Person!)
                .HasForeignKey(t => t.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Vehicles).WithOne(f => f.Person!)
                .HasForeignKey(f => f.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Locations).WithOne(o => o.Person!)
                .HasForeignKey(o => o.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Weapons).WithOne(w => w.Person!)
                .HasForeignKey(w => w.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Photos).WithOne(f => f.Person!)
                .HasForeignKey(f => f.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Docs).WithOne(d => d.Person!)
                .HasForeignKey(d => d.PersonId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Observations).WithOne(o => o.Person!)
                .HasForeignKey(o => o.PersonId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PersonAlias>(b => b.Property(a => a.AliasName).HasMaxLength(200));
        modelBuilder.Entity<PersonPhone>(b =>
        {
            b.Property(t => t.Number).HasMaxLength(40);
            b.Property(t => t.Designation).HasMaxLength(100);
            b.HasIndex(t => t.Number);
        });
        modelBuilder.Entity<PersonVehicle>(b =>
        {
            b.Property(f => f.Designation).HasMaxLength(200);
            b.Property(f => f.LicensePlate).HasMaxLength(40);
        });
        modelBuilder.Entity<PersonLocation>(b =>
        {
            b.Property(o => o.Text).HasMaxLength(300);
            b.Property(o => o.Note).HasMaxLength(500);
        });
        modelBuilder.Entity<PersonWeapon>(b => b.Property(w => w.Text).HasMaxLength(200));

        modelBuilder.Entity<ProfileSuggestion>(b =>
        {
            b.Property(v => v.Value).HasMaxLength(300).IsRequired();
            b.HasIndex(v => new { v.Type, v.Value }).IsUnique();
        });

        modelBuilder.Entity<EnumLabelOverride>(b =>
        {
            b.Property(o => o.List).HasMaxLength(50).IsRequired();
            b.Property(o => o.Key).HasMaxLength(100).IsRequired();
            b.Property(o => o.Label).HasMaxLength(200).IsRequired();
            b.HasIndex(o => new { o.List, o.Key }).IsUnique();
        });

        modelBuilder.Entity<PersonPhoto>(b =>
        {
            b.Property(f => f.FileNameSaved).HasMaxLength(128);
            b.Property(f => f.OriginalName).HasMaxLength(260);
            b.Property(f => f.ContentType).HasMaxLength(100);
            b.Property(f => f.CreatedById).HasMaxLength(64);
        });

        modelBuilder.Entity<ClassificationHistory>(b =>
        {
            b.Property(e => e.EntityType).HasMaxLength(128);
            b.Property(e => e.EntityId).HasMaxLength(64);
            b.Property(e => e.Justification).HasMaxLength(1000);
            b.Property(e => e.AgentId).HasMaxLength(64);
            b.Property(e => e.AgentName).HasMaxLength(128);
            b.Property(e => e.RequestId).HasMaxLength(64);
            b.HasIndex(e => new { e.EntityType, e.EntityId });
            // the classification-flow sankey windows on the timestamp per type
            b.HasIndex(e => new { e.EntityType, e.Timestamp });
        });

        modelBuilder.Entity<PersonDoc>(b =>
        {
            b.Property(d => d.Faction).HasMaxLength(200);
            b.Property(d => d.OrgType).HasMaxLength(128);
            b.Property(d => d.OrgId).HasMaxLength(64);
            b.HasIndex(d => d.PersonId);
            b.HasIndex(d => new { d.OrgType, d.OrgId });
            // measure trends and the outcome series window on the RP timestamp
            b.HasIndex(d => d.Timestamp);
        });

        modelBuilder.Entity<Observation>(b =>
        {
            b.Property(o => o.Location).HasMaxLength(300);
            b.Property(o => o.Sighting).HasMaxLength(4000);
            b.Property(o => o.Result).HasMaxLength(4000);
            b.Property(o => o.OrgType).HasMaxLength(128);
            b.Property(o => o.OrgId).HasMaxLength(64);
            b.HasIndex(o => o.PersonId);
            b.HasIndex(o => o.Start);
            b.HasIndex(o => new { o.OrgType, o.OrgId });
            b.HasOne(o => o.ObservingAgent).WithMany()
                .HasForeignKey(o => o.ObservingAgentId).OnDelete(DeleteBehavior.SetNull);
            b.HasIndex(o => o.ObservingAgentId);
        });

        modelBuilder.Entity<CaseNumberCounter>(b =>
        {
            b.HasKey(z => new { z.Prefix, z.Year });
            b.Property(z => z.Prefix).HasMaxLength(8);
            b.Property(z => z.Year).ValueGeneratedNever();
        });

        modelBuilder.Entity<TextImage>(b =>
        {
            b.Property(x => x.EntityType).HasMaxLength(128);
            b.Property(x => x.EntityId).HasMaxLength(64);
            b.Property(x => x.FileNameSaved).HasMaxLength(128).IsRequired();
            b.Property(x => x.ContentType).HasMaxLength(100).IsRequired();
            b.HasIndex(x => new { x.EntityType, x.EntityId });
        });

        modelBuilder.Entity<Source>(b =>
        {
            b.Property(q => q.EntityType).HasMaxLength(128);
            b.Property(q => q.EntityId).HasMaxLength(64);
            b.Property(q => q.Title).HasMaxLength(300).IsRequired();
            b.Property(q => q.Url).HasMaxLength(2048);
            b.Property(q => q.TargetType).HasMaxLength(128);
            b.Property(q => q.TargetId).HasMaxLength(64);
            b.Property(q => q.FileNameSaved).HasMaxLength(128);
            b.Property(q => q.OriginalName).HasMaxLength(260);
            b.Property(q => q.ContentType).HasMaxLength(100);
            b.HasIndex(q => new { q.EntityType, q.EntityId });
        });

        modelBuilder.Entity<Tag>(b =>
        {
            b.Property(t => t.Name).HasMaxLength(60).IsRequired();
            b.Property(t => t.Colour).HasMaxLength(32);
            b.HasIndex(t => t.Name).IsUnique();
        });

        modelBuilder.Entity<DocTemplate>(b =>
        {
            b.Property(v => v.Name).HasMaxLength(120).IsRequired();
            b.Property(v => v.Description).HasMaxLength(500);
            b.Property(v => v.DefaultReason).HasMaxLength(2000);
            b.Property(v => v.DefaultFaction).HasMaxLength(200);
            b.Property(v => v.DefaultReceivedInformation).HasMaxLength(4000);
            // no unique: soft-delete safe
            b.HasIndex(v => v.Name);
            b.HasIndex(v => v.IsActive);
        });

        // leadership-defined counter-intelligence rules (condition set as JSON)
        modelBuilder.Entity<CounterIntelRule>(b =>
        {
            b.Property(r => r.Name).HasMaxLength(150).IsRequired();
            b.Property(r => r.Description).HasMaxLength(1000);
            b.Property(r => r.DefinitionJson).HasColumnType("longtext");
            b.HasIndex(r => new { r.IsActive, r.Order });
        });

        modelBuilder.Entity<CustomFieldDefinition>(b =>
        {
            b.Property(d => d.EntityType).HasMaxLength(128).IsRequired();
            b.Property(d => d.Name).HasMaxLength(120).IsRequired();
            b.Property(d => d.Options).HasMaxLength(2000);
            // no unique: soft-delete safe
            b.HasIndex(d => new { d.EntityType, d.IsActive });
        });

        modelBuilder.Entity<CustomFieldValue>(b =>
        {
            b.Property(w => w.CustomFieldDefinitionId).HasMaxLength(64).IsRequired();
            b.Property(w => w.EntityType).HasMaxLength(128).IsRequired();
            b.Property(w => w.EntityId).HasMaxLength(64).IsRequired();
            b.Property(w => w.Value).HasColumnType("longtext");
            b.HasIndex(w => new { w.EntityType, w.EntityId });
            // one value per field per record
            b.HasIndex(w => new { w.CustomFieldDefinitionId, w.EntityType, w.EntityId }).IsUnique();
        });

        modelBuilder.Entity<Document>(b =>
        {
            b.Property(d => d.Title).HasMaxLength(300).IsRequired();
            b.Property(d => d.Category).HasMaxLength(120);
            b.Property(d => d.ContentHtml).HasColumnType("longtext");
            b.Property(d => d.OwnerTaskforceId).HasMaxLength(40);
            b.HasIndex(d => d.Title);
            b.HasIndex(d => d.Category);
            b.HasIndex(d => d.IsClassified);
            b.HasIndex(d => d.OwnerTaskforceId);
        });

        modelBuilder.Entity<ActivityTemplate>(b =>
        {
            b.Property(v => v.Name).HasMaxLength(120).IsRequired();
            b.Property(v => v.Description).HasMaxLength(500);
            b.Property(v => v.Kind).HasMaxLength(100);
            b.Property(v => v.ContentHtml).HasColumnType("longtext");
            b.HasIndex(v => v.Name);
            b.HasIndex(v => v.IsActive);
        });

        modelBuilder.Entity<DocumentTemplate>(b =>
        {
            b.Property(v => v.Name).HasMaxLength(120).IsRequired();
            b.Property(v => v.Description).HasMaxLength(500);
            b.Property(v => v.Category).HasMaxLength(120);
            b.Property(v => v.ContentHtml).HasColumnType("longtext");
            // no unique: soft-delete safe
            b.HasIndex(v => v.Name);
            b.HasIndex(v => v.IsActive);
        });

        modelBuilder.Entity<PersonnelTemplate>(b =>
        {
            b.Property(v => v.Name).HasMaxLength(120).IsRequired();
            b.Property(v => v.Description).HasMaxLength(500);
            b.Property(v => v.ContentHtml).HasColumnType("longtext");
            // uniqueness enforced per-kind in the service, not the DB (soft-delete safe)
            b.HasIndex(v => v.Kind);
            b.HasIndex(v => v.IsActive);
        });

        modelBuilder.Entity<RecencyThreshold>(b =>
        {
            // one row per type
            b.HasKey(s => s.RecordsType);
            b.Property(s => s.RecordsType).HasMaxLength(64);
        });

        // admin-tunable threat-score config (single JSON row)
        modelBuilder.Entity<ThreatScoreConfig>(b =>
        {
            b.HasKey(k => k.Id);
            b.Property(k => k.Id).HasMaxLength(32);
            b.Property(k => k.Json).HasColumnType("longtext");
        });

        modelBuilder.Entity<SituationReport>(b =>
        {
            b.Property(l => l.Title).HasMaxLength(200).IsRequired();
            b.Property(l => l.SnapshotJson).HasColumnType("longtext");
            b.Property(l => l.FinancingJson).HasColumnType("longtext");
            b.HasIndex(l => new { l.Year, l.Month });
        });

        modelBuilder.Entity<Followup>(b =>
        {
            b.Property(w => w.EntityType).HasMaxLength(128);
            b.Property(w => w.EntityId).HasMaxLength(64);
            b.Property(w => w.Note).HasMaxLength(500);
            b.Property(w => w.ResponsibleAgentId).HasMaxLength(64);
            b.Property(w => w.DoneById).HasMaxLength(64);
            b.HasIndex(w => new { w.EntityType, w.EntityId });
            b.HasIndex(w => new { w.DueAt, w.Done, w.NotifiedAt });
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(w => w.ResponsibleAgentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<TagMapping>(b =>
        {
            b.Property(z => z.EntityType).HasMaxLength(128);
            b.Property(z => z.EntityId).HasMaxLength(64);
            b.HasOne(z => z.Tag).WithMany().HasForeignKey(z => z.TagId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(z => new { z.EntityType, z.EntityId });
            // no duplicate tags
            b.HasIndex(z => new { z.TagId, z.EntityType, z.EntityId }).IsUnique();
        });

        modelBuilder.Entity<Comment>(b =>
        {
            b.Property(k => k.EntityType).HasMaxLength(128);
            b.Property(k => k.EntityId).HasMaxLength(64);
            b.Property(k => k.AuthorName).HasMaxLength(128);
            b.HasIndex(k => new { k.EntityType, k.EntityId });
        });

        modelBuilder.Entity<NOOSE_Website.Data.Entities.Search.SearchPhoneticKey>(b =>
        {
            b.Property(z => z.EntityType).HasMaxLength(128);
            b.Property(z => z.EntityId).HasMaxLength(64);
            b.Property(z => z.SourceId).HasMaxLength(64);
            b.Property(z => z.Key).HasMaxLength(NOOSE_Website.Services.SearchTokenizer.MaxPhoneticLength);
            b.HasIndex(z => z.Key);
            b.HasIndex(z => z.SourceId);
        });

        modelBuilder.Entity<NOOSE_Website.Data.Entities.Search.SearchStemToken>(b =>
        {
            b.Property(z => z.EntityType).HasMaxLength(128);
            b.Property(z => z.EntityId).HasMaxLength(64);
            b.Property(z => z.SourceId).HasMaxLength(64);
            b.Property(z => z.Stem).HasMaxLength(NOOSE_Website.Services.SearchTokenizer.MaxStemLength);
            b.HasIndex(z => z.Stem);
            b.HasIndex(z => z.SourceId);
        });

        modelBuilder.Entity<Link>(b =>
        {
            b.Property(v => v.SourceType).HasMaxLength(128);
            b.Property(v => v.SourceId).HasMaxLength(64);
            b.Property(v => v.TargetType).HasMaxLength(128);
            b.Property(v => v.TargetId).HasMaxLength(64);
            b.Property(v => v.Label).HasMaxLength(200);
            // both directions quickly findable
            b.HasIndex(v => new { v.SourceType, v.SourceId });
            b.HasIndex(v => new { v.TargetType, v.TargetId });
        });

        modelBuilder.Entity<PersonRelation>(b =>
        {
            b.Property(x => x.Note).HasMaxLength(500);
            // restrict: avoid cascade
            b.HasOne(x => x.PersonA).WithMany()
                .HasForeignKey(x => x.PersonAId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.PersonB).WithMany()
                .HasForeignKey(x => x.PersonBId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(x => x.PersonAId);
            b.HasIndex(x => x.PersonBId);
        });

        modelBuilder.Entity<SavedSearch>(b =>
        {
            b.Property(g => g.AgentId).HasMaxLength(64);
            b.Property(g => g.Name).HasMaxLength(120).IsRequired();
            b.HasOne<Agent>().WithMany().HasForeignKey(g => g.AgentId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(g => g.AgentId);
        });

        modelBuilder.Entity<TextSnippet>(b =>
        {
            b.Property(s => s.AgentId).HasMaxLength(64);
            b.Property(s => s.Name).HasMaxLength(80).IsRequired();
            b.Property(s => s.Text).HasMaxLength(4000).IsRequired();
            // cascade, like the saved search: a deleted account takes its own conveniences with it
            b.HasOne<Agent>().WithMany().HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Cascade);
            // one name per agent; the picker shows names, and two alike would be a coin toss
            b.HasIndex(s => new { s.AgentId, s.Name }).IsUnique();
        });

        modelBuilder.Entity<Faction>(b =>
        {
            b.Property(f => f.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(f => f.Name).HasMaxLength(200).IsRequired();
            b.Property(f => f.Kind).HasMaxLength(100);
            b.Property(f => f.Radio).HasMaxLength(100);
            b.Property(f => f.Darkchat).HasMaxLength(100);
            b.Property(f => f.IssuingTimes).HasMaxLength(300);
            b.Property(f => f.Estate).HasMaxLength(500);
            b.Property(f => f.RecognitionColor).HasMaxLength(32);
            b.Property(f => f.Targets).HasMaxLength(2000);
            b.Property(f => f.Description).HasMaxLength(2000);
            b.Property(f => f.ThreatDetailJson).HasColumnType("longtext");
            b.Property(f => f.ArchiveReason).HasMaxLength(300);
            b.HasIndex(f => f.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(f => f.IsArchived);
            b.HasIndex(f => f.Name);
            b.HasIndex(f => f.IsClassified);

            b.HasMany(f => f.Ranks).WithOne(r => r.Faction!)
                .HasForeignKey(r => r.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.WeaponStock).WithOne(w => w.Faction!)
                .HasForeignKey(w => w.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.Inventory).WithOne(l => l.Faction!)
                .HasForeignKey(l => l.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.DrugRoutes).WithOne(d => d.Faction!)
                .HasForeignKey(d => d.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.Members).WithOne(m => m.Faction!)
                .HasForeignKey(m => m.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.Agents).WithOne(a => a.Faction!)
                .HasForeignKey(a => a.FactionId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(f => f.Photos).WithOne(x => x.Faction!)
                .HasForeignKey(x => x.FactionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FactionPhoto>(b =>
        {
            b.Property(f => f.FileNameSaved).HasMaxLength(128);
            b.Property(f => f.OriginalName).HasMaxLength(260);
            b.Property(f => f.ContentType).HasMaxLength(100);
            b.Property(f => f.CreatedById).HasMaxLength(64);
            b.HasIndex(f => f.FactionId);
        });

        modelBuilder.Entity<PersonGroupPhoto>(b =>
        {
            b.Property(f => f.FileNameSaved).HasMaxLength(128);
            b.Property(f => f.OriginalName).HasMaxLength(260);
            b.Property(f => f.ContentType).HasMaxLength(100);
            b.Property(f => f.CreatedById).HasMaxLength(64);
            b.HasIndex(f => f.PersonGroupId);
        });

        modelBuilder.Entity<PartyPhoto>(b =>
        {
            b.Property(f => f.FileNameSaved).HasMaxLength(128);
            b.Property(f => f.OriginalName).HasMaxLength(260);
            b.Property(f => f.ContentType).HasMaxLength(100);
            b.Property(f => f.CreatedById).HasMaxLength(64);
            b.HasIndex(f => f.PartyId);
        });

        modelBuilder.Entity<FactionAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.FactionId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<FactionRank>(b =>
        {
            b.Property(r => r.Designation).HasMaxLength(100).IsRequired();
        });
        modelBuilder.Entity<FactionWeaponStock>(b =>
        {
            b.Property(w => w.Designation).HasMaxLength(200).IsRequired();
            b.Property(w => w.Quantity).HasMaxLength(50);
        });
        modelBuilder.Entity<FactionInventory>(b =>
        {
            b.Property(l => l.Designation).HasMaxLength(200).IsRequired();
            b.Property(l => l.Quantity).HasMaxLength(50);
        });
        modelBuilder.Entity<FactionDrugRoute>(b =>
        {
            b.Property(d => d.Designation).HasMaxLength(200).IsRequired();
            b.Property(d => d.Note).HasMaxLength(200);
        });

        modelBuilder.Entity<FactionMember>(b =>
        {
            b.Property(m => m.Rank).HasMaxLength(100);
            // restrict: avoid cascade
            b.HasOne(m => m.Person).WithMany()
                .HasForeignKey(m => m.PersonId).OnDelete(DeleteBehavior.Restrict);
            // unique check in service
            b.HasIndex(m => new { m.FactionId, m.PersonId });
            b.HasIndex(m => m.PersonId);
        });

        modelBuilder.Entity<PersonGroup>(b =>
        {
            b.Property(g => g.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(g => g.Name).HasMaxLength(200).IsRequired();
            b.Property(g => g.Description).HasMaxLength(2000);
            b.Property(g => g.Targets).HasMaxLength(2000);
            b.Property(g => g.ArchiveReason).HasMaxLength(300);
            b.HasIndex(g => g.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(g => g.IsArchived);
            b.HasIndex(g => g.Name);
            b.HasIndex(g => g.IsClassified);

            b.HasMany(g => g.Members).WithOne(m => m.PersonGroup!)
                .HasForeignKey(m => m.PersonGroupId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(g => g.Agents).WithOne(a => a.PersonGroup!)
                .HasForeignKey(a => a.PersonGroupId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PersonGroupMember>(b =>
        {
            b.Property(m => m.Role).HasMaxLength(100);
            // restrict: avoid cascade
            b.HasOne(m => m.Person).WithMany()
                .HasForeignKey(m => m.PersonId).OnDelete(DeleteBehavior.Restrict);
            // no unique: soft-delete safe
            b.HasIndex(m => new { m.PersonGroupId, m.PersonId });
            b.HasIndex(m => m.PersonId);
        });

        modelBuilder.Entity<PersonGroupAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.PersonGroupId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<Party>(b =>
        {
            b.Property(p => p.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.Description).HasMaxLength(2000);
            b.Property(p => p.Targets).HasMaxLength(2000);
            b.Property(p => p.Remarks).HasMaxLength(2000);
            b.Property(p => p.ArchiveReason).HasMaxLength(300);
            b.HasIndex(p => p.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(p => p.IsArchived);
            b.HasIndex(p => p.Name);
            b.HasIndex(p => p.IsClassified);

            b.HasMany(p => p.Members).WithOne(m => m.Party!)
                .HasForeignKey(m => m.PartyId).OnDelete(DeleteBehavior.Cascade);
            b.HasMany(p => p.Agents).WithOne(a => a.Party!)
                .HasForeignKey(a => a.PartyId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PartyMember>(b =>
        {
            b.Property(m => m.Role).HasMaxLength(100);
            // restrict: avoid cascade
            b.HasOne(m => m.Person).WithMany()
                .HasForeignKey(m => m.PersonId).OnDelete(DeleteBehavior.Restrict);
            // no unique: soft-delete safe
            b.HasIndex(m => new { m.PartyId, m.PersonId });
            b.HasIndex(m => m.PersonId);
        });

        modelBuilder.Entity<PartyAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.PartyId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<Operation>(b =>
        {
            b.Property(o => o.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(o => o.Title).HasMaxLength(200).IsRequired();
            b.Property(o => o.Type).HasMaxLength(100);
            b.Property(o => o.Location).HasMaxLength(200);
            b.Property(o => o.Expiry).HasMaxLength(4000);
            b.Property(o => o.Result).HasMaxLength(4000);
            b.Property(o => o.Remarks).HasMaxLength(2000);
            b.Property(o => o.ArchiveReason).HasMaxLength(300);
            b.HasIndex(o => o.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(o => o.IsArchived);
            b.HasIndex(o => o.Title);
            b.HasIndex(o => o.Status);
            b.HasIndex(o => o.IsClassified);

            b.HasMany(o => o.Agents).WithOne(a => a.Operation!)
                .HasForeignKey(a => a.OperationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OperationAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.OperationId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<AgentAbduction>(b =>
        {
            b.Property(x => x.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.PerpetratorType).HasMaxLength(128);
            b.Property(x => x.PerpetratorId).HasMaxLength(64);
            b.Property(x => x.Location).HasMaxLength(300);
            b.Property(x => x.Notes).HasColumnType("longtext");
            b.HasIndex(x => x.CaseNumber).IsUnique();
            b.HasIndex(x => x.VictimAgentId);
            b.HasIndex(x => new { x.PerpetratorType, x.PerpetratorId });
            b.HasIndex(x => x.Timestamp);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(x => x.VictimAgent).WithMany()
                .HasForeignKey(x => x.VictimAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(x => x.Compromises).WithOne(c => c.Abduction!)
                .HasForeignKey(c => c.AbductionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AbductionCompromise>(b =>
        {
            b.Property(c => c.TargetType).HasMaxLength(128);
            b.Property(c => c.TargetId).HasMaxLength(64);
            b.Property(c => c.Note).HasMaxLength(1000);
            // badge lookup: is this record currently compromised?
            b.HasIndex(c => new { c.TargetType, c.TargetId, c.Status });
            // one entry per (abduction, target); blocks concurrent duplicate inserts
            b.HasIndex(c => new { c.AbductionId, c.TargetType, c.TargetId }).IsUnique();
        });

        modelBuilder.Entity<EvidenceItem>(b =>
        {
            b.Property(i => i.Name).HasMaxLength(200).IsRequired();
            b.Property(i => i.Description).HasColumnType("longtext");
            // 300 matches ProfileSuggestion.Value, so a category survives the suggestion catalog
            b.Property(i => i.Category).HasMaxLength(300);
            b.Property(i => i.ImageFileName).HasMaxLength(200);
            b.Property(i => i.ImageContentType).HasMaxLength(100);
            b.HasIndex(i => i.Name).IsUnique();
            b.HasIndex(i => i.Category);
        });

        modelBuilder.Entity<EvidenceEntry>(b =>
        {
            b.Property(e => e.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(e => e.OwnerType).HasMaxLength(128);
            b.Property(e => e.OwnerId).HasMaxLength(64);
            b.Property(e => e.HandlerAgentId).HasMaxLength(255);
            b.Property(e => e.Notes).HasColumnType("longtext");
            b.HasIndex(e => e.CaseNumber).IsUnique();
            b.HasIndex(e => new { e.OwnerType, e.OwnerId });
            b.HasIndex(e => e.Type);
            b.HasIndex(e => e.Timestamp);
            b.HasIndex(e => e.HandlerAgentId);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(e => e.HandlerAgent).WithMany()
                .HasForeignKey(e => e.HandlerAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasMany(e => e.Lines).WithOne(l => l.Entry!)
                .HasForeignKey(l => l.EntryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvidenceEntryLine>(b =>
        {
            b.Property(l => l.Quantity);
            b.HasIndex(l => l.EntryId);
            b.HasIndex(l => l.ItemId);
            // Restrict: an item stays as long as any position references it
            b.HasOne(l => l.Item).WithMany()
                .HasForeignKey(l => l.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KassenBuchung>(b =>
        {
            b.Property(x => x.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.BookedById).HasMaxLength(255);
            b.HasIndex(x => x.CaseNumber).IsUnique();
            b.HasIndex(x => x.Account);
            b.HasIndex(x => x.Timestamp);
            b.HasIndex(x => x.BookedById);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(x => x.BookedBy).WithMany()
                .HasForeignKey(x => x.BookedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<KassenBuchungVorlage>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            b.Property(x => x.Amount).HasPrecision(18, 2);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<FinancingItem>(b =>
        {
            b.Property(x => x.Name).HasMaxLength(200).IsRequired();
            // 300 matches ProfileSuggestion.Value, so a category survives the suggestion catalog
            b.Property(x => x.Category).HasMaxLength(300);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);
            // not unique in the DB: a soft-deleted position would block reuse of its name forever,
            // and request lines reference it with Restrict, so it can never be hard-deleted.
            // The service enforces uniqueness across the live rows instead.
            b.HasIndex(x => x.Name);
            b.HasIndex(x => x.Category);
            b.HasIndex(x => x.IsActive);
        });

        modelBuilder.Entity<FinancingRequest>(b =>
        {
            b.Property(x => x.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.AgentId).HasMaxLength(255);
            b.Property(x => x.Justification).HasMaxLength(2000).IsRequired();
            b.Property(x => x.RequestedGross).HasPrecision(18, 2);
            b.Property(x => x.RequestedSubsidy).HasPrecision(18, 2);
            b.Property(x => x.ApprovedSubsidy).HasPrecision(18, 2);
            b.Property(x => x.OverrunAmount).HasPrecision(18, 2);
            b.Property(x => x.OverrunReason).HasMaxLength(500);
            b.Property(x => x.DeciderName).HasMaxLength(128);
            b.Property(x => x.DecisionNote).HasMaxLength(2000);
            b.Property(x => x.PaidByName).HasMaxLength(128);
            b.Property(x => x.KassenBuchungId).HasMaxLength(255);
            b.HasIndex(x => x.CaseNumber).IsUnique();
            b.HasIndex(x => new { x.AgentId, x.Status });
            b.HasIndex(x => x.Status);
            b.HasIndex(x => new { x.BudgetYear, x.BudgetMonth });
            // unique so the same request can never be booked twice (MySQL allows many NULLs)
            b.HasIndex(x => x.KassenBuchungId).IsUnique();
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancingRequestLine>(b =>
        {
            b.Property(x => x.RequestId).HasMaxLength(255).IsRequired();
            b.Property(x => x.ItemName).HasMaxLength(200).IsRequired();
            b.Property(x => x.Category).HasMaxLength(300);
            b.Property(x => x.UnitPrice).HasPrecision(18, 2);
            b.HasIndex(x => x.RequestId);
            b.HasIndex(x => x.ItemId);
            b.HasOne(x => x.Request).WithMany(r => r.Lines)
                .HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: a catalog position stays as long as any line references it
            b.HasOne(x => x.Item).WithMany()
                .HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FinancingBudgetPeriod>(b =>
        {
            b.Property(x => x.AgentId).HasMaxLength(255);
            b.Property(x => x.BaseBudget).HasPrecision(18, 2);
            b.Property(x => x.CarryIn).HasPrecision(18, 2);
            b.Property(x => x.Consumed).HasPrecision(18, 2);
            b.Property(x => x.CarryOut).HasPrecision(18, 2);
            // carries the race safety of the period close; orphaned periods share a NULL agent, which MySQL
            // does not count as a duplicate
            b.HasIndex(x => new { x.AgentId, x.Year, x.Month }).IsUnique();
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LlmRequestLog>(b =>
        {
            b.Property(x => x.AgentId).HasMaxLength(255).IsRequired();
            b.Property(x => x.Model).HasMaxLength(128);
            b.Property(x => x.Provider).HasMaxLength(64);
            b.Property(x => x.ErrorMessage).HasMaxLength(500);
            b.Property(x => x.PromptFingerprint).HasMaxLength(64);
            b.Property(x => x.FinishReason).HasMaxLength(32);
            b.Property(x => x.CostUsd).HasPrecision(18, 8);
            b.Property(x => x.Prompt).HasColumnType("longtext");
            b.Property(x => x.Answer).HasColumnType("longtext");
            b.Property(x => x.ContextRefsJson).HasColumnType("longtext");
            // the hot path: the weekly consumption sum behind every pre-flight check
            b.HasIndex(x => new { x.AgentId, x.BudgetYear, x.BudgetWeek });
            // per-agent time windows: burn rate, burst, personnel tile
            b.HasIndex(x => new { x.AgentId, x.CreatedAt });
            // admin list ordering and the global rolling average
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.Feature, x.CreatedAt });
            // cheap "only anomalies" filter in the log viewer
            b.HasIndex(x => new { x.IsAnomalous, x.CreatedAt });
            // first pass of the near-identical-prompt rule
            b.HasIndex(x => new { x.AgentId, x.PromptFingerprint });
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LlmQuotaPeriod>(b =>
        {
            b.Property(x => x.AgentId).HasMaxLength(255).IsRequired();
            // carries the race safety of the period close
            b.HasIndex(x => new { x.AgentId, x.Year, x.Week }).IsUnique();
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LlmQuotaAdjustment>(b =>
        {
            b.Property(x => x.AgentId).HasMaxLength(255).IsRequired();
            b.Property(x => x.Reason).HasMaxLength(300).IsRequired();
            b.Property(x => x.CreatedByName).HasMaxLength(128);
            b.HasIndex(x => new { x.AgentId, x.Year, x.Week });
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NooseiConversation>(b =>
        {
            b.Property(x => x.AgentId).HasMaxLength(255).IsRequired();
            b.Property(x => x.Title).HasMaxLength(200).IsRequired();
            b.Property(x => x.AnchorEntityType).HasMaxLength(128);
            b.Property(x => x.AnchorEntityId).HasMaxLength(64);
            b.Property(x => x.ScopeStamp).HasMaxLength(64);
            // the owner's list, newest first
            b.HasIndex(x => new { x.AgentId, x.LastMessageAt });
            b.HasOne(x => x.Agent).WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NooseiMessage>(b =>
        {
            b.Property(x => x.ConversationId).HasMaxLength(64).IsRequired();
            b.Property(x => x.Role).HasMaxLength(16).IsRequired();
            b.Property(x => x.ToolCallId).HasMaxLength(64);
            b.Property(x => x.ToolName).HasMaxLength(64);
            b.Property(x => x.UnsupportedCitations).HasMaxLength(300);
            b.Property(x => x.Content).HasColumnType("longtext");
            b.Property(x => x.ToolCallsJson).HasColumnType("longtext");
            b.Property(x => x.SourcesJson).HasColumnType("longtext");
            b.HasIndex(x => new { x.ConversationId, x.Sequence }).IsUnique();
            // a message has no life without its thread
            b.HasOne(x => x.Conversation).WithMany()
                .HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentActivity>(b =>
        {
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.Kind).HasMaxLength(100);
            b.Property(a => a.ContentHtml).HasColumnType("longtext");
            b.HasIndex(a => a.Title);
            b.HasIndex(a => a.Kind);
            b.HasIndex(a => a.ActivityDate);
            b.HasIndex(a => a.CreatedById);

            b.HasMany(a => a.Links).WithOne(l => l.AgentActivity!)
                .HasForeignKey(l => l.AgentActivityId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentActivityLink>(b =>
        {
            b.Property(l => l.TargetType).HasMaxLength(64).IsRequired();
            b.Property(l => l.TargetId).HasMaxLength(64).IsRequired();
            // fast path for the faction/group activities tab
            b.HasIndex(l => new { l.TargetType, l.TargetId });
            b.HasIndex(l => new { l.AgentActivityId, l.TargetType, l.TargetId }).IsUnique();
        });

        modelBuilder.Entity<Case>(b =>
        {
            b.Property(v => v.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(v => v.Title).HasMaxLength(200).IsRequired();
            b.Property(v => v.Type).HasMaxLength(100);
            b.Property(v => v.Description).HasMaxLength(4000);
            b.Property(v => v.Summary).HasMaxLength(4000);
            b.Property(v => v.ClosingNote).HasMaxLength(4000);
            b.Property(v => v.ArchiveReason).HasMaxLength(300);
            b.HasIndex(v => v.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(v => v.IsArchived);
            b.HasIndex(v => v.Title);
            b.HasIndex(v => v.Status);
            b.HasIndex(v => v.IsClassified);
            // cycle time and the open-vs-closed flow window on the completion date
            b.HasIndex(v => v.CompletedAt);

            b.HasMany(v => v.Agents).WithOne(a => a.Case!)
                .HasForeignKey(a => a.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaseAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.CaseId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<Job>(b =>
        {
            b.Property(a => a.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.Description).HasMaxLength(4000);
            b.HasIndex(a => a.CaseNumber).IsUnique();
            b.HasIndex(a => a.Title);
            b.HasIndex(a => a.Status);
            b.HasIndex(a => a.DueDate);
            b.HasIndex(a => a.IsRestricted);

            b.HasMany(a => a.Assignments).WithOne(z => z.Job!)
                .HasForeignKey(z => z.JobId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<JobAssignment>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(z => z.Agent).WithMany()
                .HasForeignKey(z => z.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(z => new { z.JobId, z.AgentId }).IsUnique();
            b.HasIndex(z => z.AgentId);
        });

        modelBuilder.Entity<Appointment>(b =>
        {
            b.Property(t => t.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(t => t.Title).HasMaxLength(200).IsRequired();
            b.Property(t => t.Location).HasMaxLength(200);
            b.Property(t => t.Description).HasMaxLength(4000);
            b.HasIndex(t => t.CaseNumber).IsUnique();
            b.HasIndex(t => t.Title);
            b.HasIndex(t => t.Category);
            b.HasIndex(t => t.Status);
            b.HasIndex(t => t.Start);
            b.HasIndex(t => t.Visibility);

            b.HasMany(t => t.Participant).WithOne(z => z.Appointment!)
                .HasForeignKey(z => z.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppointmentAssignment>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(z => z.Agent).WithMany()
                .HasForeignKey(z => z.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(z => new { z.AppointmentId, z.AgentId }).IsUnique();
            b.HasIndex(z => z.AgentId);
        });

        // ---- absences & meetings ----
        modelBuilder.Entity<Absence>(b =>
        {
            b.Property(a => a.AgentId).HasMaxLength(64).IsRequired();
            b.Property(a => a.Reason).HasMaxLength(500);
            b.Property(a => a.AcknowledgedById).HasMaxLength(64);
            b.Property(a => a.AcknowledgedByName).HasMaxLength(128);

            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(a => a.AcknowledgedById).OnDelete(DeleteBehavior.SetNull);

            // "is X away on day D" plus the agent's own list
            b.HasIndex(a => new { a.AgentId, a.FromDate, a.ToDate });
            // "who is away on day D", no agent predicate
            b.HasIndex(a => new { a.FromDate, a.ToDate, a.AgentId });
            b.HasIndex(a => a.AcknowledgedAt);
        });

        modelBuilder.Entity<Feedback>(b =>
        {
            b.Property(f => f.AgentId).HasMaxLength(64).IsRequired();
            b.Property(f => f.PageRoute).HasMaxLength(128);
            b.Property(f => f.PageTab).HasMaxLength(64);
            b.Property(f => f.Text).HasMaxLength(2000).IsRequired();
            b.Property(f => f.Response).HasMaxLength(2000);
            b.Property(f => f.DeciderName).HasMaxLength(128);

            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(f => f.Agent).WithMany()
                .HasForeignKey(f => f.AgentId).OnDelete(DeleteBehavior.Restrict);

            // the agent's own list
            b.HasIndex(f => f.AgentId);
        });

        modelBuilder.Entity<Meeting>(b =>
        {
            b.Property(m => m.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(m => m.Title).HasMaxLength(200).IsRequired();
            b.Property(m => m.Location).HasMaxLength(200);
            b.Property(m => m.MinutesHtml).HasColumnType("longtext");
            b.Property(m => m.PreviousMeetingId).HasMaxLength(64);

            b.HasIndex(m => m.CaseNumber).IsUnique();
            b.HasIndex(m => m.Title);
            // reminder worker plus list sorting
            b.HasIndex(m => new { m.Start, m.Status });
            // anomaly evaluation window
            b.HasIndex(m => new { m.Status, m.AttendanceClosedAt, m.Start });

            // one-to-one clone chain; the unique index makes a double clone impossible
            b.HasOne<Meeting>().WithOne()
                .HasForeignKey<Meeting>(m => m.PreviousMeetingId).OnDelete(DeleteBehavior.Restrict);

            b.HasMany(m => m.AgendaItems).WithOne(p => p.Meeting!)
                .HasForeignKey(p => p.MeetingId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MeetingAgendaItem>(b =>
        {
            b.Property(p => p.MeetingId).HasMaxLength(64).IsRequired();
            b.Property(p => p.Title).HasMaxLength(300).IsRequired();
            b.Property(p => p.NotesHtml).HasColumnType("longtext");
            b.Property(p => p.DoneById).HasMaxLength(64);
            b.Property(p => p.CarriedFromItemId).HasMaxLength(64);

            b.HasIndex(p => new { p.MeetingId, p.Sorting });
        });

        modelBuilder.Entity<MeetingSignOff>(b =>
        {
            b.Property(s => s.MeetingId).HasMaxLength(64).IsRequired();
            b.Property(s => s.AgentId).HasMaxLength(64).IsRequired();
            b.Property(s => s.Reason).HasMaxLength(500);

            b.HasOne(s => s.Meeting).WithMany()
                .HasForeignKey(s => s.MeetingId).OnDelete(DeleteBehavior.Cascade);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(s => s.Agent).WithMany()
                .HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(s => new { s.MeetingId, s.AgentId }).IsUnique();
        });

        modelBuilder.Entity<MeetingAttendance>(b =>
        {
            b.Property(t => t.MeetingId).HasMaxLength(64).IsRequired();
            b.Property(t => t.AgentId).HasMaxLength(64).IsRequired();
            b.Property(t => t.AgentCodename).HasMaxLength(128);
            b.Property(t => t.Reason).HasMaxLength(500);
            b.Property(t => t.MarkedById).HasMaxLength(64);

            b.HasOne(t => t.Meeting).WithMany()
                .HasForeignKey(t => t.MeetingId).OnDelete(DeleteBehavior.Cascade);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(t => t.Agent).WithMany()
                .HasForeignKey(t => t.AgentId).OnDelete(DeleteBehavior.Restrict);

            b.HasIndex(t => new { t.MeetingId, t.AgentId }).IsUnique();
            b.HasIndex(t => t.AgentId);
        });

        // ---- announcements ----
        modelBuilder.Entity<Announcement>(b =>
        {
            b.Property(a => a.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.TargetId).HasMaxLength(64);
            b.HasIndex(a => a.CaseNumber).IsUnique();
            b.HasIndex(a => new { a.Important, a.CreatedAt });
            b.HasIndex(a => a.Audience);

            b.HasMany(a => a.Acknowledgments).WithOne(q => q.Announcement!)
                .HasForeignKey(q => q.AnnouncementId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AnnouncementAcknowledgment>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(q => q.Agent).WithMany()
                .HasForeignKey(q => q.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(q => new { q.AnnouncementId, q.AgentId }).IsUnique();
            b.HasIndex(q => new { q.AgentId, q.AcknowledgedAt });
        });

        modelBuilder.Entity<Request>(b =>
        {
            b.Property(a => a.TargetType).HasMaxLength(128);
            b.Property(a => a.TargetId).HasMaxLength(64);
            b.Property(a => a.TargetDesignation).HasMaxLength(256);
            b.Property(a => a.Justification).HasMaxLength(2000);
            b.Property(a => a.DecisionNote).HasMaxLength(2000);
            b.Property(a => a.RequesterName).HasMaxLength(100);
            b.Property(a => a.DeciderName).HasMaxLength(100);
            b.Property(a => a.CreatedById).HasMaxLength(64);
            b.Property(a => a.PublicationWantedId).HasMaxLength(64);
            b.Property(a => a.BountyShareId).HasMaxLength(64);
            b.HasIndex(a => new { a.Type, a.Status });
            b.HasIndex(a => new { a.TargetType, a.TargetId });
            b.HasIndex(a => a.CreatedById);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.Property(n => n.RecipientId).HasMaxLength(64);
            b.Property(n => n.Title).HasMaxLength(300);
            b.Property(n => n.Href).HasMaxLength(512);
            b.Property(n => n.CreatedById).HasMaxLength(64);
            b.HasIndex(n => new { n.RecipientId, n.ReadAt });
        });

        modelBuilder.Entity<WatchlistEntry>(b =>
        {
            b.Property(w => w.AgentId).HasMaxLength(64);
            b.Property(w => w.EntityType).HasMaxLength(128);
            b.Property(w => w.EntityId).HasMaxLength(64);
            b.Property(w => w.CreatedById).HasMaxLength(64);
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne<Agent>().WithMany().HasForeignKey(w => w.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(w => new { w.EntityType, w.EntityId });
            b.HasIndex(w => new { w.AgentId, w.IsDeleted });
        });

        modelBuilder.Entity<Taskforce>(b =>
        {
            b.Property(t => t.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(t => t.Name).HasMaxLength(200).IsRequired();
            b.Property(t => t.Purpose).HasMaxLength(4000);
            b.Property(t => t.Remarks).HasMaxLength(2000);
            b.Property(t => t.ArchiveReason).HasMaxLength(300);
            b.HasIndex(t => t.CaseNumber).IsUnique();
            // the stock listings ask for this on every page load
            b.HasIndex(t => t.IsArchived);
            b.HasIndex(t => t.Name);
            b.HasIndex(t => t.Status);
            b.HasIndex(t => t.IsClassified);

            b.HasMany(t => t.Agents).WithOne(a => a.Taskforce!)
                .HasForeignKey(a => a.TaskforceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RadioChannel>(b =>
        {
            b.Property(c => c.Frequency).HasMaxLength(32).IsRequired();
            b.Property(c => c.Label).HasMaxLength(200).IsRequired();
            b.Property(c => c.Note).HasMaxLength(500);
            b.Property(c => c.TaskforceId).HasMaxLength(64);
            // the reverse lookup asks for exactly this
            b.HasIndex(c => c.Frequency);
            b.HasIndex(c => c.Scope);
            b.HasIndex(c => c.TaskforceId);
            // a bound taskforce must be deleted through its own path, not through this row
            b.HasOne<Taskforce>().WithMany().HasForeignKey(c => c.TaskforceId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskforceAgent>(b =>
        {
            // Restrict FK to identity Agent (no cascade from the user table)
            b.HasOne(a => a.Agent).WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(a => new { a.TaskforceId, a.AgentId }).IsUnique();
            b.HasIndex(a => a.AgentId);
        });

        modelBuilder.Entity<TaskforceMessage>(b =>
        {
            b.Property(n => n.Text).HasMaxLength(4000).IsRequired();
            b.Property(n => n.AuthorName).HasMaxLength(128);
            b.HasIndex(n => new { n.TaskforceId, n.CreatedAt });
            b.HasOne(n => n.Taskforce).WithMany()
                .HasForeignKey(n => n.TaskforceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AgentRankHistory>(b =>
        {
            b.Property(v => v.ActorName).HasMaxLength(128);
            b.Property(v => v.Reason).HasMaxLength(500);
            b.HasIndex(v => new { v.AgentId, v.Timestamp });
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(v => v.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentNote>(b =>
        {
            b.Property(v => v.Text).HasColumnType("longtext").IsRequired();
            b.Property(v => v.AuthorName).HasMaxLength(128);
            b.Property(v => v.ArtFreetext).HasMaxLength(128);
            b.Property(v => v.Ausfuehrende).HasColumnType("longtext");
            b.HasIndex(v => new { v.AgentId, v.Kind });
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(v => v.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentPromotionRequest>(b =>
        {
            b.Property(a => a.Justification).HasColumnType("longtext");
            b.Property(a => a.RequesterName).HasMaxLength(128);
            b.Property(a => a.DeciderName).HasMaxLength(128);
            b.Property(a => a.DecisionNote).HasMaxLength(2000);
            b.HasIndex(a => new { a.AgentId, a.Status });
            b.HasIndex(a => a.Status);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(a => a.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TrainingModule>(b =>
        {
            b.Property(m => m.Name).HasMaxLength(160).IsRequired();
            b.Property(m => m.Description).HasMaxLength(2000);
            b.HasIndex(m => m.Name);
        });

        modelBuilder.Entity<AgentModuleCompletion>(b =>
        {
            b.Property(c => c.CompleterName).HasMaxLength(128);
            b.Property(c => c.Note).HasMaxLength(2000);
            b.HasIndex(c => new { c.AgentId, c.ModuleId });
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(c => c.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(c => c.Module).WithMany()
                .HasForeignKey(c => c.ModuleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SystemSetting>(b =>
        {
            b.HasKey(e => e.Key);
            b.Property(e => e.Key).HasMaxLength(128);
        });

        modelBuilder.Entity<Law>(b =>
        {
            b.Property(g => g.LawBook).HasMaxLength(128).IsRequired();
            b.Property(g => g.Paragraph).HasMaxLength(32).IsRequired();
            b.Property(g => g.Title).HasMaxLength(256).IsRequired();
            b.Property(g => g.Sentence).HasMaxLength(512);
            b.Property(g => g.Section).HasMaxLength(256);
            b.HasIndex(g => g.LawBook);
            b.HasIndex(g => g.Title);
            // the public law page reads by this flag alone
            b.HasIndex(g => g.IsPublic);
        });

        modelBuilder.Entity<LawBook>(b =>
        {
            b.Property(g => g.Abbreviation).HasMaxLength(128).IsRequired();
            b.Property(g => g.Name).HasMaxLength(256).IsRequired();
            b.Property(g => g.Description).HasMaxLength(1024);
            // not unique: a book in the trash keeps its abbreviation
            b.HasIndex(g => g.Abbreviation);
        });

        modelBuilder.Entity<LibraryFile>(b =>
        {
            b.Property(d => d.Title).HasMaxLength(300).IsRequired();
            b.Property(d => d.Category).HasMaxLength(120);
            b.Property(d => d.OriginalName).HasMaxLength(260).IsRequired();
            b.Property(d => d.FileNameSaved).HasMaxLength(128).IsRequired();
            b.Property(d => d.ContentType).HasMaxLength(100).IsRequired();
            b.HasIndex(d => d.Title);
            b.HasIndex(d => d.Category);
            b.HasIndex(d => d.IsClassified);
        });

        modelBuilder.Entity<PartnerShare>(b =>
        {
            b.Property(s => s.EntityType).HasMaxLength(128).IsRequired();
            b.Property(s => s.EntityId).HasMaxLength(64).IsRequired();
            b.Property(s => s.PartnerAgentId).HasMaxLength(64);
            b.HasIndex(s => new { s.EntityType, s.EntityId });
            b.HasIndex(s => new { s.Agency, s.EntityType, s.EntityId });
        });

        modelBuilder.Entity<DocumentAccessExclusion>(b =>
        {
            b.Property(x => x.DocumentId).HasMaxLength(64).IsRequired();
            b.Property(x => x.AgentId).HasMaxLength(64).IsRequired();
            // no unique: soft-delete safe; dedupe in service among active rows
            b.HasIndex(x => new { x.DocumentId, x.AgentId });
            b.HasIndex(x => x.AgentId);
            b.HasOne<Document>().WithMany()
                .HasForeignKey(x => x.DocumentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(x => x.AgentId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<AgentInvite>(b =>
        {
            b.Property(i => i.Token).HasMaxLength(64).IsRequired();
            b.Property(i => i.Label).HasMaxLength(200);
            b.Property(i => i.CreatedByName).HasMaxLength(128);
            b.Property(i => i.UsedByUserId).HasMaxLength(64);
            b.HasIndex(i => i.Token).IsUnique();
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(i => i.UsedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bewerbung>(b =>
        {
            b.Property(v => v.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(v => v.AcademicDegree).HasMaxLength(64);
            b.Property(v => v.Name).HasMaxLength(200).IsRequired();
            b.Property(v => v.Employer).HasMaxLength(200);
            b.Property(v => v.PriorExperience).HasColumnType("longtext");
            b.Property(v => v.CoverLetter).HasColumnType("longtext");
            b.Property(v => v.AttachmentFileNameSaved).HasMaxLength(128);
            b.Property(v => v.AttachmentOriginalName).HasMaxLength(260);
            b.Property(v => v.AttachmentContentType).HasMaxLength(100);
            b.Property(v => v.AssignedAgentName).HasMaxLength(128);
            b.Property(v => v.LinkedPersonId).HasMaxLength(64);
            b.Property(v => v.LinkedCaseId).HasMaxLength(64);
            b.Property(v => v.DecisionNote).HasColumnType("longtext");
            b.Property(v => v.DecidedByName).HasMaxLength(128);
            b.HasIndex(v => v.CaseNumber).IsUnique();
            b.HasIndex(v => v.Status);
            b.HasIndex(v => new { v.ApplicantUserId, v.Status });
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(v => v.ApplicantUserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(v => v.AssignedAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Person>().WithMany()
                .HasForeignKey(v => v.LinkedPersonId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Case>().WithMany()
                .HasForeignKey(v => v.LinkedCaseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Bewerbungssperre>(b =>
        {
            b.Property(s => s.AgentId).HasMaxLength(64).IsRequired();
            b.Property(s => s.DiscordId).HasMaxLength(64);
            b.Property(s => s.ApplicantName).HasMaxLength(200);
            b.Property(s => s.BewerbungId).HasMaxLength(64);
            b.Property(s => s.Reason).HasColumnType("longtext");
            b.Property(s => s.CreatedByName).HasMaxLength(128);
            b.HasIndex(s => s.AgentId);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(s => s.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Bewerbung>().WithMany()
                .HasForeignKey(s => s.BewerbungId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BewerbungMessage>(b =>
        {
            b.Property(m => m.Text).HasColumnType("longtext");
            b.Property(m => m.AuthorName).HasMaxLength(128);
            b.HasIndex(m => new { m.BewerbungId, m.Audience });
            b.HasIndex(m => new { m.BewerbungId, m.CreatedAt });
            b.HasOne(m => m.Bewerbung).WithMany()
                .HasForeignKey(m => m.BewerbungId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BewerbungTest>(b =>
        {
            b.Property(t => t.Title).HasMaxLength(200).IsRequired();
            b.Property(t => t.Description).HasColumnType("longtext");
            b.HasIndex(t => t.IsActive);
        });

        modelBuilder.Entity<BewerbungTestQuestion>(b =>
        {
            b.Property(q => q.Prompt).HasColumnType("longtext");
            b.Property(q => q.Points).HasDefaultValue(1);
            b.Property(q => q.Keywords).HasColumnType("longtext");
            b.HasIndex(q => new { q.TestId, q.Sorting });
            b.HasOne(q => q.Test).WithMany()
                .HasForeignKey(q => q.TestId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BewerbungTestOption>(b =>
        {
            b.Property(o => o.Label).HasMaxLength(500).IsRequired();
            b.HasIndex(o => new { o.QuestionId, o.Sorting });
            b.HasOne(o => o.Question).WithMany()
                .HasForeignKey(o => o.QuestionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BewerbungTestAssignment>(b =>
        {
            b.Property(a => a.AssignedByName).HasMaxLength(128);
            b.Property(a => a.GradedByName).HasMaxLength(128);
            b.HasIndex(a => a.BewerbungId).IsUnique();
            // the expiry sweep's predicate, null-check first and the range last
            b.HasIndex(a => new { a.CompletedAt, a.DeadlineAt });
            b.Property(a => a.AttemptCount).HasDefaultValue(1);
            b.HasOne(a => a.Bewerbung).WithMany()
                .HasForeignKey(a => a.BewerbungId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Test).WithMany()
                .HasForeignKey(a => a.TestId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BewerbungTestAnswer>(b =>
        {
            b.Property(a => a.SelectedOptionId).HasMaxLength(64);
            // a guid list, not one id: an option-rich question would overflow a 64-char column
            b.Property(a => a.SelectedOptionIds).HasMaxLength(2048);
            b.Property(a => a.FreeTextAnswer).HasColumnType("longtext");
            b.HasIndex(a => new { a.AssignmentId, a.QuestionId });
            b.HasOne(a => a.Assignment).WithMany()
                .HasForeignKey(a => a.AssignmentId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(a => a.Question).WithMany()
                .HasForeignKey(a => a.QuestionId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<BuergerProfil>(b =>
        {
            b.Property(p => p.UserId).HasMaxLength(64);
            b.Property(p => p.FirstName).HasMaxLength(64);
            b.Property(p => p.LastName).HasMaxLength(64);
            b.Property(p => p.BlockedReason).HasColumnType("longtext");
            b.Property(p => p.BlockedById).HasMaxLength(64);
            b.Property(p => p.LinkedPersonId).HasMaxLength(64);
            // one citizen profile per identity user; a deleted account leaves the profile with a NULL here,
            // and MySQL allows repeated NULLs in a unique index
            b.HasIndex(p => p.UserId).IsUnique();
            b.HasIndex(p => p.IsBlocked);
            b.HasOne(p => p.User).WithMany()
                .HasForeignKey(p => p.UserId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Agent>().WithMany()
                .HasForeignKey(p => p.BlockedById).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Person>().WithMany()
                .HasForeignKey(p => p.LinkedPersonId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OeffentlichesModul>(b =>
        {
            b.Property(m => m.Key).HasMaxLength(64).IsRequired();
            b.Property(m => m.OfflineText).HasColumnType("longtext");
            b.Property(m => m.LabelOverride).HasMaxLength(64);
            // an icon name from the allowlist, not the SVG itself
            b.Property(m => m.IconOverride).HasMaxLength(64);
            // one row per catalog key; a duplicate would let two switches fight over one module
            b.HasIndex(m => m.Key).IsUnique();
        });

        modelBuilder.Entity<OeffentlicheSeite>(b =>
        {
            b.Property(p => p.Slug).HasMaxLength(64).IsRequired();
            b.Property(p => p.Title).HasMaxLength(200).IsRequired();
            b.Property(p => p.MenuTitle).HasMaxLength(64);
            // an icon name from the allowlist, not the SVG itself
            b.Property(p => p.IconName).HasMaxLength(64);
            b.Property(p => p.ContentHtml).HasColumnType("longtext");
            b.Property(p => p.DraftHtml).HasColumnType("longtext");
            b.Property(p => p.PublishedById).HasMaxLength(64);
            // NOT unique: a soft-deleted page keeps its slug, and a unique index would block reusing the address
            b.HasIndex(p => p.Slug);
            b.HasIndex(p => p.Status);
            b.HasOne(p => p.PublishedBy).WithMany()
                .HasForeignKey(p => p.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OeffentlicheFaqRubrik>(b =>
        {
            b.Property(r => r.Title).HasMaxLength(160).IsRequired();
            // plain text, not HTML: one line under the heading, rendered as text
            b.Property(r => r.Description).HasMaxLength(400);
            // an icon name from the allowlist, not the SVG itself
            b.Property(r => r.IconName).HasMaxLength(64);
            // the only shape the public read knows: the visible rubrics, in order
            b.HasIndex(r => new { r.IsVisible, r.SortOrder });
        });

        modelBuilder.Entity<OeffentlicheFaqEintrag>(b =>
        {
            b.Property(e => e.RubrikId).HasMaxLength(64).IsRequired();
            b.Property(e => e.Question).HasMaxLength(300).IsRequired();
            // the deep-link handle; a slug, so it is validated on write instead of escaped on read
            b.Property(e => e.Anchor).HasMaxLength(64).IsRequired();
            b.Property(e => e.AnswerHtml).HasColumnType("longtext");
            // Restrict, so the database says what the service says: a rubric that still holds questions is
            // refused rather than taking several rich-text answers down with it, and these rows are hard-deleted
            b.HasOne(e => e.Rubrik).WithMany(r => r.Entries)
                .HasForeignKey(e => e.RubrikId).OnDelete(DeleteBehavior.Restrict);
            // unique, unlike the page slug: that one may not be, because a soft-deleted page keeps its address.
            // A question is hard-deleted, so nothing blocks an anchor a live one could want back
            b.HasIndex(e => e.Anchor).IsUnique();
            // the only shape the public read knows: the visible questions of one rubric, in order
            b.HasIndex(e => new { e.RubrikId, e.IsVisible, e.SortOrder });
        });

        modelBuilder.Entity<Pressemitteilung>(b =>
        {
            b.Property(p => p.CaseNumber).HasMaxLength(32);
            b.Property(p => p.Title).HasMaxLength(200).IsRequired();
            b.Property(p => p.Teaser).HasMaxLength(400).IsRequired();
            b.Property(p => p.ContentTitle).HasMaxLength(200);
            b.Property(p => p.ContentTeaser).HasMaxLength(400);
            b.Property(p => p.ContentHtml).HasColumnType("longtext");
            b.Property(p => p.DraftHtml).HasColumnType("longtext");
            b.Property(p => p.PublishedById).HasMaxLength(64);
            // unique, unlike the page slug: a counter number is never reused, so a soft-deleted row may keep its own
            b.HasIndex(p => p.CaseNumber).IsUnique();
            b.HasIndex(p => new { p.Status, p.PublishedAt });
            b.HasOne(p => p.PublishedBy).WithMany()
                .HasForeignKey(p => p.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OeffentlicheWarnung>(b =>
        {
            b.Property(w => w.Title).HasMaxLength(200).IsRequired();
            b.Property(w => w.ContentTitle).HasMaxLength(200);
            b.Property(w => w.ContentHtml).HasColumnType("longtext");
            b.Property(w => w.DraftHtml).HasColumnType("longtext");
            b.Property(w => w.PublishedById).HasMaxLength(64);
            // the read path filters on both at once
            b.HasIndex(w => new { w.Status, w.ValidUntil });
            b.HasOne(w => w.PublishedBy).WithMany()
                .HasForeignKey(w => w.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OeffentlicherLagebericht>(b =>
        {
            b.Property(r => r.Title).HasMaxLength(200).IsRequired();
            b.Property(r => r.ContentTitle).HasMaxLength(200);
            b.Property(r => r.ContentHtml).HasColumnType("longtext");
            b.Property(r => r.DraftHtml).HasColumnType("longtext");
            b.Property(r => r.SituationReportId).HasMaxLength(64);
            b.Property(r => r.PublishedById).HasMaxLength(64);
            // the hub filters on the status and orders by the period
            b.HasIndex(r => new { r.Status, r.Year, r.Month });
            // optional on purpose: a required navigation is INNER joined, so a report whose anchor was deleted would
            // drop out of the panel projection while a count that touches no navigation kept counting it
            b.HasOne(r => r.SituationReport).WithMany()
                .HasForeignKey(r => r.SituationReportId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(r => r.PublishedBy).WithMany()
                .HasForeignKey(r => r.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HandbookChapter>(b =>
        {
            b.Property(c => c.Slug).HasMaxLength(80).IsRequired();
            b.Property(c => c.Title).HasMaxLength(160).IsRequired();
            b.Property(c => c.Description).HasMaxLength(400);
            b.Property(c => c.IconName).HasMaxLength(64);
            b.Property(c => c.SeedKey).HasMaxLength(128);
            b.HasIndex(c => c.Slug).IsUnique();
            b.HasIndex(c => c.SeedKey).IsUnique();
            // the only shape the rail reads: visible chapters, in order
            b.HasIndex(c => new { c.IsVisible, c.SortOrder });
        });

        modelBuilder.Entity<HandbookArticle>(b =>
        {
            b.Property(a => a.ChapterId).HasMaxLength(64).IsRequired();
            // stays 120, the width the migration created. The real limit is 64 — the slug is this article's key
            // in the search side index, whose EntityId columns are varchar(64), and a longer one fails that
            // insert and takes the whole SaveChanges with it — but that rule lives in HandbookService.Slug,
            // which every write goes through. Narrowing it here without a migration would only put the model
            // and the database out of step: the column would stay 120 on MySQL, EnsureCreated would build 64
            // in the tests, and the next scaffold would offer a surprise ALTER.
            b.Property(a => a.Slug).HasMaxLength(120).IsRequired();
            b.Property(a => a.Title).HasMaxLength(200).IsRequired();
            b.Property(a => a.Summary).HasMaxLength(400);
            b.Property(a => a.ContentHtml).HasColumnType("longtext");
            b.Property(a => a.RoleplayHtml).HasColumnType("longtext");
            b.Property(a => a.DiagramKey).HasMaxLength(64);
            b.Property(a => a.NavKey).HasMaxLength(64);
            b.Property(a => a.SeedKey).HasMaxLength(128);
            b.HasIndex(a => a.Slug).IsUnique();
            b.HasIndex(a => a.SeedKey).IsUnique();
            b.HasIndex(a => new { a.ChapterId, a.IsVisible, a.SortOrder });
            // the help button resolves a route to a menu key, then a key to one article
            b.HasIndex(a => a.NavKey);
            // Restrict, not Cascade: a chapter is soft-deleted, and a cascade into an audited table would
            // remove its articles without a single audit row
            b.HasOne(a => a.Chapter).WithMany(c => c.Articles)
                .HasForeignKey(a => a.ChapterId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<HandbookStep>(b =>
        {
            b.Property(s => s.ArticleId).HasMaxLength(64).IsRequired();
            b.Property(s => s.Title).HasMaxLength(200).IsRequired();
            b.Property(s => s.Text).HasMaxLength(600).IsRequired();
            b.Property(s => s.IconName).HasMaxLength(64);
            b.HasIndex(s => new { s.ArticleId, s.Number });
            // owned rows: a step has no life without its article, and the seeder replaces them wholesale
            b.HasOne(s => s.Article).WithMany(a => a.Steps)
                .HasForeignKey(s => s.ArticleId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GlossaryTerm>(b =>
        {
            b.Property(t => t.Term).HasMaxLength(120).IsRequired();
            b.Property(t => t.Synonyms).HasMaxLength(400);
            b.Property(t => t.ShortDefinition).HasMaxLength(400).IsRequired();
            b.Property(t => t.ExplanationHtml).HasColumnType("longtext");
            b.Property(t => t.ArticleId).HasMaxLength(64);
            b.Property(t => t.SeedKey).HasMaxLength(128);
            b.HasIndex(t => t.Term).IsUnique();
            b.HasIndex(t => t.SeedKey).IsUnique();
            b.HasIndex(t => t.IsVisible);
            // optional on purpose: a required navigation is INNER joined, so a term whose article was deleted
            // would silently drop out of the glossary
            b.HasOne(t => t.Article).WithMany()
                .HasForeignKey(t => t.ArticleId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ChangelogRelease>(b =>
        {
            b.Property(r => r.Version).HasMaxLength(32).IsRequired();
            b.Property(r => r.Title).HasMaxLength(200);
            b.Property(r => r.BuildNumber).HasMaxLength(32);
            // authored, so a unique index is safe: the release has no soft delete that could hold a version hostage
            b.HasIndex(r => r.Version).IsUnique();
            // the only shape the page reads: visible releases, newest first
            b.HasIndex(r => new { r.IsVisible, r.Date, r.SortOrder });
        });

        modelBuilder.Entity<ChangelogEntry>(b =>
        {
            b.Property(e => e.ReleaseId).HasMaxLength(64).IsRequired();
            b.Property(e => e.Title).HasMaxLength(300).IsRequired();
            b.Property(e => e.Area).HasMaxLength(64);
            b.Property(e => e.SeedKey).HasMaxLength(128);
            // unique so a shipped line cannot be seeded twice; filtered rows keep theirs, which is what stops the
            // seeder from re-creating an entry somebody deleted on purpose
            b.HasIndex(e => e.SeedKey).IsUnique();
            b.HasIndex(e => new { e.ReleaseId, e.IsVisible, e.SortOrder });
            // Restrict, not Cascade: a release is never deleted, and a cascade path into an audited table would
            // remove lines without a single audit row
            b.HasOne(e => e.Release).WithMany(r => r.Entries)
                .HasForeignKey(e => e.ReleaseId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<OeffentlicheFahndung>(b =>
        {
            b.Property(f => f.CaseNumber).HasMaxLength(32);
            b.Property(f => f.DisplayName).HasMaxLength(200).IsRequired();
            b.Property(f => f.AliasText).HasMaxLength(400);
            b.Property(f => f.PhotoFileName).HasMaxLength(128);
            b.Property(f => f.PhotoContentType).HasMaxLength(64);
            b.Property(f => f.PhotoSourceId).HasMaxLength(64);
            b.Property(f => f.ChargeHtml).HasColumnType("longtext");
            b.Property(f => f.LastArea).HasMaxLength(200);
            b.Property(f => f.VehicleText).HasMaxLength(400);
            b.Property(f => f.RetractedReason).HasColumnType("longtext");
            b.Property(f => f.PersonId).HasMaxLength(64);
            b.Property(f => f.FactionId).HasMaxLength(64);
            b.Property(f => f.PublishedById).HasMaxLength(64);
            b.Property(f => f.PublicPaidOut).HasPrecision(18, 2);
            // unique, unlike the page slug: a counter number is issued once and never reused, so a
            // soft-deleted row blocks no address anyone could want back
            b.HasIndex(f => f.CaseNumber).IsUnique();
            b.HasIndex(f => new { f.Status, f.PublishedAt });
            b.HasIndex(f => f.PersonId);
            b.HasIndex(f => f.FactionId);
            b.HasIndex(f => f.ExpiresAt);
            b.HasOne(f => f.Person).WithMany()
                .HasForeignKey(f => f.PersonId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(f => f.Faction).WithMany()
                .HasForeignKey(f => f.FactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(f => f.PublishedBy).WithMany()
                .HasForeignKey(f => f.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Warnhinweis>(b =>
        {
            b.Property(w => w.Name).HasMaxLength(60).IsRequired();
            // a colour NAME from the allowlist, not a free value: this one is rendered anonymously
            b.Property(w => w.Colour).HasMaxLength(32);
            b.HasIndex(w => w.Name).IsUnique();
            b.HasIndex(w => new { w.IsActive, w.SortOrder });
        });

        modelBuilder.Entity<FahndungWarnhinweis>(b =>
        {
            b.Property(z => z.FahndungId).HasMaxLength(64);
            b.Property(z => z.WarnhinweisId).HasMaxLength(64);
            // cascade on both ends: the row says nothing without either of them, and it is what keeps the unique
            // index on Bezeichnung usable after a hard delete. No cascade-path clash — the notice itself hangs on
            // Personen/Fraktionen/Agent with Restrict, so the chain ends at OeffentlicheFahndungen
            b.HasOne(z => z.Fahndung).WithMany()
                .HasForeignKey(z => z.FahndungId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(z => z.Warnhinweis).WithMany()
                .HasForeignKey(z => z.WarnhinweisId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(z => new { z.FahndungId, z.WarnhinweisId }).IsUnique();
        });

        modelBuilder.Entity<FahndungKopfgeldAnteil>(b =>
        {
            b.Property(k => k.WantedId).HasMaxLength(64);
            b.Property(k => k.DonorAgentId).HasMaxLength(64);
            b.Property(k => k.KassenBuchungId).HasMaxLength(255);
            b.Property(k => k.Amount).HasPrecision(18, 2);
            b.Property(k => k.WithdrawnReason).HasMaxLength(500);
            // Restrict, unlike the warning assignment: a money trail must not disappear with its target, and the
            // notice is only ever soft-deleted anyway
            b.HasOne(k => k.Wanted).WithMany()
                .HasForeignKey(k => k.WantedId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(k => k.DonorAgent).WithMany()
                .HasForeignKey(k => k.DonorAgentId).OnDelete(DeleteBehavior.Restrict);
            // the sum path
            b.HasIndex(k => new { k.WantedId, k.Status });
            // one booking backs at most one share, mirroring FinancingRequest
            b.HasIndex(k => k.KassenBuchungId).IsUnique();
        });

        modelBuilder.Entity<HinweisBelohnung>(b =>
        {
            b.Property(x => x.ReceiptNumber).HasMaxLength(32).IsRequired();
            b.Property(x => x.TipId).HasMaxLength(64);
            b.Property(x => x.ShareId).HasMaxLength(64);
            b.Property(x => x.KassenBuchungId).HasMaxLength(255);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            // Restrict for the same reason as the share: a payment trail must not disappear with its target
            b.HasOne(x => x.Tip).WithMany()
                .HasForeignKey(x => x.TipId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(x => x.Share).WithMany()
                .HasForeignKey(x => x.ShareId).OnDelete(DeleteBehavior.Restrict);
            // not unique: one tip may draw from several shares, and its rows share the receipt
            b.HasIndex(x => x.ReceiptNumber);
            b.HasIndex(x => x.TipId);
            b.HasIndex(x => x.ShareId);
            b.HasIndex(x => x.KassenBuchungId).IsUnique();
        });

        modelBuilder.Entity<Hinweis>(b =>
        {
            b.Property(h => h.PriorityOverrideReason).HasMaxLength(500);
            // kept in step with CaptureRules.MaxLocationLength; Data does not reach into Services
            b.Property(h => h.HandoverLocation).HasMaxLength(200);
            b.Property(h => h.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(h => h.CitizenProfileId).HasMaxLength(64);
            b.Property(h => h.WantedId).HasMaxLength(64);
            b.Property(h => h.HandlerId).HasMaxLength(64);
            b.Property(h => h.DuplicateGroupId).HasMaxLength(64);
            b.Property(h => h.AnonymityResolvedById).HasMaxLength(64);
            b.Property(h => h.Text).HasColumnType("longtext");
            b.Property(h => h.AttachmentFileName).HasMaxLength(128);
            b.Property(h => h.AttachmentOriginalName).HasMaxLength(255);
            b.Property(h => h.AttachmentContentType).HasMaxLength(128);
            // Restrict throughout: a tip outlives the account, the notice and the handler it names
            b.HasOne(h => h.CitizenProfile).WithMany()
                .HasForeignKey(h => h.CitizenProfileId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(h => h.Wanted).WithMany()
                .HasForeignKey(h => h.WantedId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(h => h.Handler).WithMany()
                .HasForeignKey(h => h.HandlerId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(h => h.CaseNumber).IsUnique();
            // the inbox order: priority first, creation time as the tie-break
            b.HasIndex(h => new { h.Status, h.Priority });
            b.HasIndex(h => new { h.Status, h.CreatedAt });
            // the rate-limit count
            b.HasIndex(h => new { h.CitizenProfileId, h.CreatedAt });
            b.HasIndex(h => h.WantedId);
            // duplicate groups: the sibling list and the per-group count
            b.HasIndex(h => h.DuplicateGroupId);
        });

        modelBuilder.Entity<HinweisNachricht>(b =>
        {
            b.Property(m => m.HinweisId).HasMaxLength(64);
            b.Property(m => m.AuthorAgentId).HasMaxLength(64);
            b.Property(m => m.Text).HasColumnType("longtext");
            b.HasOne(m => m.Hinweis).WithMany()
                .HasForeignKey(m => m.HinweisId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.AuthorAgent).WithMany()
                .HasForeignKey(m => m.AuthorAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.HinweisId, m.Audience });
            b.HasIndex(m => new { m.HinweisId, m.CreatedAt });
        });

        modelBuilder.Entity<Ticket>(b =>
        {
            b.Property(t => t.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(t => t.CitizenProfileId).HasMaxLength(64);
            b.Property(t => t.Subject).HasMaxLength(160).IsRequired();
            b.Property(t => t.HandlerId).HasMaxLength(64);
            b.Property(t => t.OpenedByAgentId).HasMaxLength(64);
            b.Property(t => t.ClosedById).HasMaxLength(64);
            b.Property(t => t.ClosingNote).HasMaxLength(1000);
            // Restrict throughout: a ticket outlives the account and the handler it names
            b.HasOne(t => t.CitizenProfile).WithMany()
                .HasForeignKey(t => t.CitizenProfileId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.Handler).WithMany()
                .HasForeignKey(t => t.HandlerId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(t => t.OpenedByAgent).WithMany()
                .HasForeignKey(t => t.OpenedByAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(t => t.CaseNumber).IsUnique();
            // the desk splits citizen from internal before it sorts
            b.HasIndex(t => new { t.Kind, t.Status, t.LastActivityAt });
            // the desk order: newest activity first within a status tab
            b.HasIndex(t => new { t.Status, t.LastActivityAt });
            // both caps of the quota read these
            b.HasIndex(t => new { t.CitizenProfileId, t.Status });
            b.HasIndex(t => new { t.CitizenProfileId, t.CreatedAt });
            // the desk filters by concern inside a status tab, and the KPI counts per concern
            b.HasIndex(t => new { t.Category, t.Status, t.LastActivityAt });
        });

        modelBuilder.Entity<OeffentlichesFuehrungsprofil>(b =>
        {
            b.Property(p => p.PublicKey).HasMaxLength(64).IsRequired();
            b.Property(p => p.AgentId).HasMaxLength(64);
            b.Property(p => p.DisplayName).HasMaxLength(128).IsRequired();
            b.Property(p => p.Title).HasMaxLength(128).IsRequired();
            b.Property(p => p.RoleText).HasMaxLength(160);
            b.Property(p => p.PhotoFileName).HasMaxLength(256);
            b.Property(p => p.PhotoContentType).HasMaxLength(128);
            b.Property(p => p.PublishedById).HasMaxLength(64);
            // Restrict: a released entry outlives the account it was made from, and it carries its own copy of
            // every value anyway
            b.HasOne(p => p.Agent).WithMany()
                .HasForeignKey(p => p.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(p => p.PublicKey).IsUnique();
            b.HasIndex(p => new { p.PublishedAt, p.SortOrder });
        });

        modelBuilder.Entity<TicketParticipant>(b =>
        {
            b.Property(p => p.TicketId).HasMaxLength(64);
            b.Property(p => p.AgentId).HasMaxLength(64);
            // Cascade off the ticket, Restrict off the account: the assignment dies with its ticket, never with
            // the agent — the same split TicketNachricht draws
            b.HasOne(p => p.Ticket).WithMany()
                .HasForeignKey(p => p.TicketId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Agent).WithMany()
                .HasForeignKey(p => p.AgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(p => new { p.TicketId, p.AgentId }).IsUnique();
            b.HasIndex(p => p.AgentId);
        });

        modelBuilder.Entity<TicketNachricht>(b =>
        {
            b.Property(m => m.TicketId).HasMaxLength(64);
            b.Property(m => m.AuthorAgentId).HasMaxLength(64);
            b.Property(m => m.Text).HasColumnType("longtext");
            b.Property(m => m.AttachmentFileName).HasMaxLength(128);
            b.Property(m => m.AttachmentOriginalName).HasMaxLength(255);
            b.Property(m => m.AttachmentContentType).HasMaxLength(128);
            b.HasOne(m => m.Ticket).WithMany()
                .HasForeignKey(m => m.TicketId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(m => m.AuthorAgent).WithMany()
                .HasForeignKey(m => m.AuthorAgentId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(m => new { m.TicketId, m.Audience });
            b.HasIndex(m => new { m.TicketId, m.CreatedAt });
        });

        modelBuilder.Entity<OeffentlicheVorlage>(b =>
        {
            b.Property(v => v.Title).HasMaxLength(160).IsRequired();
            // longtext: the body is a message, and a length cap here would truncate it without a word
            b.Property(v => v.Text).HasColumnType("longtext");
            // the only shape the read path knows: the active templates of one kind, in order
            b.HasIndex(v => new { v.Kind, v.IsActive, v.SortOrder });
        });

        modelBuilder.Entity<OeffentlichesFraktionsprofil>(b =>
        {
            b.Property(p => p.DisplayName).HasMaxLength(200).IsRequired();
            // longtext: the description may carry images, and a cap here would truncate it without a word
            b.Property(p => p.DescriptionHtml).HasColumnType("longtext");
            b.Property(p => p.RetractedReason).HasColumnType("longtext");
            b.Property(p => p.FactionId).HasMaxLength(64);
            b.Property(p => p.PublishedById).HasMaxLength(64);
            // No unique index on FraktionId: with soft delete it would block the faction forever, the same trap the
            // page slug documents. "One live profile per faction" is enforced over the living rows in the service.
            b.HasIndex(p => new { p.Status, p.PublishedAt });
            b.HasIndex(p => p.FactionId);
            b.HasOne(p => p.Faction).WithMany()
                .HasForeignKey(p => p.FactionId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(p => p.PublishedBy).WithMany()
                .HasForeignKey(p => p.PublishedById).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FahndungEinspruch>(b =>
        {
            b.Property(e => e.CaseNumber).HasMaxLength(32).IsRequired();
            b.Property(e => e.WantedId).HasMaxLength(64);
            b.Property(e => e.CitizenProfileId).HasMaxLength(64);
            b.Property(e => e.DecidedById).HasMaxLength(64);
            b.Property(e => e.LinkedCaseId).HasMaxLength(64);
            // longtext on both: an objection is an argument, and a cap would truncate it without a word
            b.Property(e => e.Text).HasColumnType("longtext");
            b.Property(e => e.DecisionNote).HasColumnType("longtext");
            // Restrict throughout: an objection outlives the account and the agent it names, and deleting a notice
            // out from under it would erase what was disputed
            b.HasOne(e => e.Wanted).WithMany()
                .HasForeignKey(e => e.WantedId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(e => e.CitizenProfile).WithMany()
                .HasForeignKey(e => e.CitizenProfileId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(e => e.DecidedBy).WithMany()
                .HasForeignKey(e => e.DecidedById).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(e => e.LinkedCase).WithMany()
                .HasForeignKey(e => e.LinkedCaseId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(e => e.CaseNumber).IsUnique();
            // the desk order: newest first within a status tab
            b.HasIndex(e => new { e.Status, e.CreatedAt });
            // the two caps and the banner on a notice read these
            b.HasIndex(e => new { e.CitizenProfileId, e.CreatedAt });
            b.HasIndex(e => new { e.WantedId, e.Status });
        });

        // global soft-delete filter
        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(t => typeof(ISoftDelete).IsAssignableFrom(t.ClrType))
                     .ToList())
        {
            SetSoftDeleteFilterMethod
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(null, new object[] { modelBuilder });
        }
    }

    private static readonly MethodInfo SetSoftDeleteFilterMethod =
        typeof(AppDbContext).GetMethod(nameof(SetSoftDeleteFilter),
            BindingFlags.NonPublic | BindingFlags.Static)!;

    private static void SetSoftDeleteFilter<TEntity>(ModelBuilder builder)
        where TEntity : class, ISoftDelete
    {
        builder.Entity<TEntity>().HasQueryFilter(e => !e.IsDeleted);
    }
}
