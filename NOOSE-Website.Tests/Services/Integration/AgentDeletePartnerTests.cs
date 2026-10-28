using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Evidence;
using NOOSE_Website.Data.Entities.Feedback;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Gamification;
using NOOSE_Website.Data.Entities.Informants;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>
/// Hard account delete against ENFORCED foreign keys. <see cref="SqliteTestContext"/> runs with
/// <c>PRAGMA foreign_keys = OFF</c>, so the rest of the suite cannot see a Restrict pointer blocking the delete —
/// the failure it produces in MySQL ("An error occurred while saving the entity changes") is invisible there.
/// This fixture keeps enforcement on, so a missing cleanup in <c>DeleteAccountAsync</c> fails here for real.
/// </summary>
public sealed class AgentDeletePartnerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<AppDbContext> _options;

    public AgentDeletePartnerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:;Foreign Keys=True");
        _connection.Open();
        _options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(_connection)
            .ConfigureWarnings(w => w.Ignore(RelationalEventId.AmbientTransactionWarning))
            .Options;
        using var db = new AppDbContext(_options);
        db.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    private sealed class Factory(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    private AppDbContext NewContext() => new(_options);

    private static UserManager<Agent> BuildUserManager(AppDbContext db)
        => new(new UserStore<Agent>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<Agent>(), Array.Empty<IUserValidator<Agent>>(),
            Array.Empty<IPasswordValidator<Agent>>(), new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);

    private AgentManagementService BuildService(AppDbContext db)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Path.Combine(Path.GetTempPath(), "noose-delete-tests",
            Guid.NewGuid().ToString("N")));
        var avatars = new AgentAvatarStorageService(env, Options.Create(new FileUploadOptions()));
        return new AgentManagementService(BuildUserManager(db), db, new Factory(_options),
            Substitute.For<INotificationService>(), Substitute.For<IDiscordWebhookService>(),
            new ConfigurationBuilder().Build(), avatars, Substitute.For<IPublicLeadershipPhotoStorageService>());
    }

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    /// <summary>Foreign-key enforcement only bites on a real INSERT, so every row is committed before the delete.</summary>
    private void SeedPartnerWithEverything(string agentId)
    {
        using var db = NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent(agentId, Rank.SpecialAgent, configure: a =>
        {
            a.PartnerAgency = PartnerAgency.DoJ;
        }));

        var document = new Document { Title = "Akte" };
        var meeting = new Meeting { CaseNumber = "NOOSE-BES-2026-0001", Title = "Lage" };
        db.Documents.Add(document);
        db.Meetings.Add(meeting);

        // the citizen identity a partner gets by filing a ticket or a tip, plus the history hanging off it
        var profile = new BuergerProfil { UserId = agentId, FirstName = "Ben", LastName = "Partner" };
        db.BuergerProfile.Add(profile);
        var ticket = new Ticket
        {
            CaseNumber = "NOOSE-TIC-2026-0001",
            Kind = TicketArt.Fuehrungsebene,
            CitizenProfileId = profile.Id,
            OpenedByAgentId = agentId,
            HandlerId = agentId,
            Subject = "Frage",
        };
        db.Tickets.Add(ticket);
        db.TicketNachrichten.Add(new TicketNachricht
        {
            TicketId = ticket.Id, Text = "Antwort", AuthorAgentId = agentId,
        });
        db.TicketBeteiligte.Add(new TicketParticipant { TicketId = ticket.Id, AgentId = agentId });
        db.Hinweise.Add(new Hinweis
        {
            CaseNumber = "NOOSE-HIN-2026-0001",
            CitizenProfileId = profile.Id,
            Text = "Beobachtung",
            HandlerId = agentId,
        });

        // the account's own working state
        db.Absences.Add(new Absence
        {
            AgentId = agentId,
            FromDate = new DateOnly(2026, 3, 1),
            ToDate = new DateOnly(2026, 3, 5),
            Days = 5,
        });
        db.Feedbacks.Add(new Feedback { AgentId = agentId, Text = "Idee" });
        db.AgentBadges.Add(new AgentBadge { AgentId = agentId, BadgeKey = "erste-akte" });
        db.DocumentAccessExclusions.Add(new DocumentAccessExclusion
        {
            DocumentId = document.Id, AgentId = agentId,
        });
        db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = meeting.Id, AgentId = agentId });
        db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = meeting.Id, AgentId = agentId });
        db.LlmQuotaPeriods.Add(new LlmQuotaPeriod { AgentId = agentId, Year = 2026, Week = 10 });
        db.LlmQuotaAdjustments.Add(new LlmQuotaAdjustment
        {
            AgentId = agentId, Year = 2026, Week = 10, Tokens = 500, Reason = "Nachschlag",
        });
        db.LlmRequests.Add(new LlmRequestLog { AgentId = agentId, BudgetYear = 2026, BudgetWeek = 10 });
        var conversation = new NooseiConversation { AgentId = agentId, Title = "Frage an NOOSEI" };
        db.NooseiConversations.Add(conversation);
        db.NooseiMessages.Add(new NooseiMessage
        {
            ConversationId = conversation.Id, Sequence = 1, Role = "user", Content = "Hallo",
        });

        // no cleanup covers these three: the FK itself carries the behaviour, so the schema has to be right
        var person = Seed.Person("person-1");
        db.People.Add(person);
        db.Observations.Add(new Observation { PersonId = person.Id, ObservingAgentId = agentId });
        db.Followups.Add(new Followup
        {
            EntityType = nameof(Person), EntityId = person.Id, ResponsibleAgentId = agentId,
        });
        db.SavedSearch.Add(new SavedSearch { AgentId = agentId, Name = "Meine Suche" });

        // records that outlive the account and only carry it as a pointer
        db.EvidenceEntries.Add(new EvidenceEntry
        {
            CaseNumber = "NOOSE-ASS-2026-0001", OwnerType = "NOOSE", HandlerAgentId = agentId,
        });
        db.Informants.Add(new Informant { CaseNumber = "NOOSE-INF-2026-0001", HandlerId = agentId });
        db.AgentAbductions.Add(new AgentAbduction
        {
            CaseNumber = "NOOSE-ENT-2026-0001", VictimAgentId = agentId,
        });
        db.FinancingRequests.Add(new FinancingRequest
        {
            CaseNumber = "NOOSE-FIN-2026-0001", AgentId = agentId, Justification = "Ausrüstung",
        });
        db.FinancingBudgetPeriods.Add(new FinancingBudgetPeriod
        {
            AgentId = agentId, Year = 2026, Month = 3,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task DeleteAccountAsync_PartnerWithCitizenIdentityAndHistory_Succeeds()
    {
        const string agentId = "partner-1";
        SeedPartnerWithEverything(agentId);

        using var db = NewContext();
        await BuildService(db).DeleteAccountAsync(agentId, Leader());

        using var check = NewContext();
        Assert.Null(await check.Users.FindAsync(agentId));
    }

    [Fact]
    public async Task DeleteAccountAsync_Partner_DropsOwnStateAndKeepsHistoryWithoutPointer()
    {
        const string agentId = "partner-2";
        SeedPartnerWithEverything(agentId);

        using var db = NewContext();
        await BuildService(db).DeleteAccountAsync(agentId, Leader());

        using var check = NewContext();

        // own working state is gone
        Assert.Empty(await check.Absences.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.Feedbacks.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.AgentBadges.ToListAsync());
        Assert.Empty(await check.DocumentAccessExclusions.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.MeetingAttendances.ToListAsync());
        Assert.Empty(await check.MeetingSignOffs.ToListAsync());
        Assert.Empty(await check.LlmQuotaPeriods.ToListAsync());
        Assert.Empty(await check.LlmQuotaAdjustments.ToListAsync());
        Assert.Empty(await check.LlmRequests.ToListAsync());
        Assert.Empty(await check.NooseiConversations.IgnoreQueryFilters().ToListAsync());
        Assert.Empty(await check.NooseiMessages.ToListAsync());
        // the row IS the permission to read that ticket, so it goes rather than being nulled
        Assert.Empty(await check.TicketBeteiligte.ToListAsync());

        // the tip carries an anonymity promise and the objection gate reads the profile: both outlive the account
        var profile = Assert.Single(await check.BuergerProfile.IgnoreQueryFilters().ToListAsync());
        Assert.Null(profile.UserId);
        var tip = Assert.Single(await check.Hinweise.IgnoreQueryFilters().ToListAsync());
        Assert.Null(tip.HandlerId);
        Assert.Equal(profile.Id, tip.CitizenProfileId);

        var ticket = Assert.Single(await check.Tickets.IgnoreQueryFilters().ToListAsync());
        Assert.Null(ticket.HandlerId);
        Assert.Null(ticket.OpenedByAgentId);
        var message = Assert.Single(await check.TicketNachrichten.IgnoreQueryFilters().ToListAsync());
        Assert.Null(message.AuthorAgentId);

        // these three ride on the FK definition rather than on the cleanup: SetNull, SetNull, Cascade
        Assert.Null(Assert.Single(await check.Observations.IgnoreQueryFilters().ToListAsync()).ObservingAgentId);
        Assert.Null(Assert.Single(await check.Followups.IgnoreQueryFilters().ToListAsync()).ResponsibleAgentId);
        Assert.Empty(await check.SavedSearch.ToListAsync());

        Assert.Null(Assert.Single(await check.EvidenceEntries.IgnoreQueryFilters().ToListAsync()).HandlerAgentId);
        Assert.Null(Assert.Single(await check.Informants.IgnoreQueryFilters().ToListAsync()).HandlerId);
        Assert.Null(Assert.Single(await check.AgentAbductions.IgnoreQueryFilters().ToListAsync()).VictimAgentId);
        Assert.Null(Assert.Single(await check.FinancingRequests.IgnoreQueryFilters().ToListAsync()).AgentId);
        Assert.Null(Assert.Single(await check.FinancingBudgetPeriods.ToListAsync()).AgentId);
    }

    /// <summary>The forensic trail is the whole point of a hard delete, so it has to survive the user row.</summary>
    [Fact]
    public async Task DeleteAccountAsync_Partner_LeavesAnAuditRow()
    {
        const string agentId = "partner-3";
        SeedPartnerWithEverything(agentId);

        using var db = NewContext();
        await BuildService(db).DeleteAccountAsync(agentId, Leader());

        using var check = NewContext();
        var row = await check.AuditLogs
            .Where(a => a.EntityType == nameof(Agent) && a.EntityId == agentId)
            .SingleAsync();
        Assert.Equal(AuditAction.Deleted, row.Action);
    }
}
