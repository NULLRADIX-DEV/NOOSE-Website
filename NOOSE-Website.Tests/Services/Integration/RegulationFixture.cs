using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Regulation;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Shared builders of the regulation tests: service, principals, rows.</summary>
internal static class RegulationFixture
{
    public const string StaleText = "Inzwischen hat jemand eine neuere Fassung veröffentlicht. Lade die Seite neu.";
    public static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    public static DateOnly Today => MeetingTime.Day(DateTime.UtcNow);

    public sealed record Built(RegulationService Svc, INotificationService Notifications, List<string> Reported);

    /// <summary>The service with the audit interceptor, so a removal soft-deletes as in production.</summary>
    public static Built Build(SqliteTestContext ctx, params IInterceptor[] extra)
    {
        IInterceptor[] interceptors = [new AuditSaveChangesInterceptor(new FixedUser()), .. extra];
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).AddInterceptors(interceptors).Options;
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new AcknowledgmentBroadcaster();
        var reported = new List<string>();
        broadcaster.Modified += id => reported.Add(id);
        var svc = new RegulationService(new TestDbContextFactory(options), notifications, broadcaster,
            NullLogger<RegulationService>.Instance);
        return new Built(svc, notifications, reported);
    }

    // ---------- principals ----------

    public static ClaimsPrincipal Boss(string id = "boss")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falke").Build();

    public static ClaimsPrincipal Plain(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    public static ClaimsPrincipal Chief(string id, Division division, bool hrb = false)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SectionChief).AsDivision(division).AsHrb(hrb)
            .AsTru(division == Division.Hrt).Build();

    public static ClaimsPrincipal Supervision(string id = "sup")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    public static ClaimsPrincipal Partner(string id = "partner")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    /// <summary>The session of a seeded row.</summary>
    public static ClaimsPrincipal PrincipalOf(Agent agent)
    {
        var builder = ClaimsPrincipalBuilder.Agent(agent.Id).WithStatus(agent.Status)
            .AsTru(agent.IsTRU).AsHrb(agent.IsHRB).AsAdmin(agent.IsAdmin).AsTeamLead(agent.IsTeamLead);
        if (agent.Rank is { } rank)
        {
            builder.WithRank(rank);
        }
        if (agent.Division is { } division)
        {
            builder.AsDivision(division);
        }
        if (agent.PartnerAgency is { } agency)
        {
            builder.AsPartner(agency, PartnerRank.Chief);
        }
        return builder.Build();
    }

    // ---------- inputs ----------

    public static RegulationParagraphInput Input(string heading = "Funkdisziplin",
        RegulationAudience audience = RegulationAudience.Bureau, string html = "<p>Eins zwei drei.</p>")
        => new(heading, audience, html, Today.AddDays(-1), "Erstfassung");

    public static RegulationVersionInput Next(int expected, RegulationAudience audience = RegulationAudience.Bureau,
        string html = "<p>Eins zwei vier.</p>", bool requires = true, DateOnly? validFrom = null, string heading = "Funkdisziplin")
        => new(heading, audience, html, validFrom ?? Today, "Neufassung", requires, expected);

    // ---------- rows ----------

    public static async Task SeedAgentsAsync(SqliteTestContext ctx, params Agent[] agents)
    {
        await using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
    }

    public static async Task SetAgentAsync(SqliteTestContext ctx, string id, Action<Agent> change)
    {
        await using var db = ctx.NewContext();
        change(await db.Users.SingleAsync(u => u.Id == id));
        await db.SaveChangesAsync();
    }

    public static RegulationSection SectionRow(string id, int order, string? title = null, bool preamble = false,
        Action<RegulationSection>? configure = null)
    {
        var section = new RegulationSection
        {
            Id = id, Title = title ?? "Abschnitt " + id, SortOrder = order, IsPreamble = preamble, CreatedAt = Stamp,
        };
        configure?.Invoke(section);
        return section;
    }

    public static RegulationParagraph ParagraphRow(string id, string sectionId, string? heading = null,
        RegulationAudience audience = RegulationAudience.Bureau, Action<RegulationParagraph>? configure = null)
    {
        var paragraph = new RegulationParagraph
        {
            Id = id,
            SectionId = sectionId,
            Heading = heading ?? "Absatz " + id,
            Audience = audience,
            SortOrder = 10,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
            CreatedAt = Stamp,
        };
        configure?.Invoke(paragraph);
        return paragraph;
    }

    public static RegulationParagraphVersion VersionRow(string paragraphId, int version, DateTime? publishedAt = null,
        string html = "<p>Text</p>", RegulationAudience audience = RegulationAudience.Bureau, string heading = "Absatz")
        => new()
        {
            ParagraphId = paragraphId,
            Version = version,
            Heading = heading,
            Audience = audience,
            ContentHtml = html,
            ValidFrom = Today.AddDays(-30),
            ChangeNote = "Notiz",
            RequiresAcknowledgment = true,
            PublishedAt = publishedAt ?? Stamp,
            CreatedAt = Stamp,
        };

    public static RegulationAcknowledgment AckRow(string paragraphId, string agentId, int version)
        => new() { ParagraphId = paragraphId, AgentId = agentId, Version = version, AcknowledgedAt = Stamp, CreatedAt = Stamp };

    /// <summary>A live section "s1" (number 1) and nothing else.</summary>
    public static async Task<string> SeedSectionAsync(SqliteTestContext ctx, string id = "s1", string title = "Allgemeines")
    {
        await using var db = ctx.NewContext();
        db.RegulationSections.Add(SectionRow(id, 10, title));
        await db.SaveChangesAsync();
        return id;
    }

    public static async Task<List<string>> OwingAsync(SqliteTestContext ctx, string paragraphId)
    {
        await using var db = ctx.NewContext();
        return (await RegulationDebt.Owed(db).Where(r => r.ParagraphId == paragraphId).Select(r => r.AgentId)
            .ToListAsync()).Order().ToList();
    }

    public static Task RingsNobodyAsync(INotificationService notifications)
        => notifications.DidNotReceive().NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

    public sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("boss", "Falke", true, false, false);
    }
}
