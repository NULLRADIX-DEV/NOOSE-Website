using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Directives;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Badge, shift list and quota read one debt predicate; they must never disagree.</summary>
public sealed class DirectiveDebtTests
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = MeetingTime.Day(DateTime.UtcNow);

    public static TheoryData<DocumentClassification> Levels() => new()
    {
        DocumentClassification.None,
        DocumentClassification.Leadership,
        DocumentClassification.Tru,
        DocumentClassification.Hrb,
    };

    /// <summary>One agent as the database holds it and as the session carries it.</summary>
    private sealed record Member(Agent Account, ClaimsPrincipal Principal);

    private static (DirectiveService Svc, INotificationService Notifications) Build(SqliteTestContext ctx,
        params IInterceptor[] interceptors)
    {
        var options = interceptors.Length == 0
            ? ctx.Options
            : new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).AddInterceptors(interceptors).Options;
        var notifications = Substitute.For<INotificationService>();
        var svc = new DirectiveService(new TestDbContextFactory(options), notifications, new AcknowledgmentBroadcaster(),
            NullLogger<DirectiveService>.Instance);
        return (svc, notifications);
    }

    private static ClaimsPrincipal Boss(string id = "boss")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falke").Build();

    private static ClaimsPrincipal Plain(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static DirectiveInput Input(string title = "Waffenmitführung",
        DocumentClassification level = DocumentClassification.None)
        => new(title, null, level, "<p>Eins zwei drei.</p>", Today.AddDays(-1), "Erstfassung");

    private static DirectiveVersionInput Next(int expected, DocumentClassification level = DocumentClassification.None,
        bool requires = true)
        => new("Waffenmitführung", null, level, "<p>Eins zwei vier.</p>", Today, "Neufassung", requires, expected);

    private static async Task SeedAgentsAsync(SqliteTestContext ctx, params Agent[] agents)
    {
        await using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
    }

    /// <summary>Seven kinds of account around a creator: every one the debt could wrongly catch.</summary>
    private static List<Member> Cast() =>
    [
        new Member(Seed.Agent("lead", Rank.SectionChief),
            ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).Build()),
        new Member(Seed.Agent("tru", Rank.SpecialAgent, configure: a => a.IsTRU = true),
            ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().Build()),
        new Member(Seed.Agent("hrb", Rank.SpecialAgent, configure: a => a.IsHRB = true),
            ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build()),
        new Member(Seed.Agent("plain", Rank.Trainee), Plain("plain")),
        new Member(Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true),
            ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.Director).AsTeamLead().Build()),
        new Member(Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
            ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build()),
        new Member(Seed.Agent("gone", Rank.Trainee, AgentStatus.Terminated),
            ClaimsPrincipalBuilder.Agent("gone").WithRank(Rank.Trainee).WithStatus(AgentStatus.Terminated).Build()),
    ];

    /// <summary>Who is in the audience of a directive at a level, the creator "boss" included.</summary>
    private static string[] AudienceOf(DocumentClassification level) => level switch
    {
        DocumentClassification.Leadership => ["boss", "lead"],
        DocumentClassification.Tru => ["boss", "lead", "tru"],
        DocumentClassification.Hrb => ["boss", "hrb", "lead"],
        _ => ["boss", "hrb", "lead", "plain", "tru"],
    };

    /// <summary>The audience minus the creator, who wrote the first version and so owes nothing.</summary>
    private static string[] OwedBy(DocumentClassification level) => AudienceOf(level).Where(id => id != "boss").ToArray();

    private static async Task AssertAgreementAsync(DirectiveService svc, List<Member> cast, string directiveId,
        string[] audience, string[] owed, string message)
    {
        var quota = await svc.GetQuotaAsync(directiveId, Boss());
        Assert.NotNull(quota);
        Assert.True(quota.Total == audience.Length, $"{message}: total {quota.Total}");
        Assert.Equal(owed, quota.Missing.Select(m => m.AgentId).Order());
        Assert.Equal(audience.Except(owed).Order(), quota.Done.Select(d => d.AgentId).Order());
        Assert.Equal(audience.Length - owed.Length, quota.Acknowledged);

        foreach (var person in cast)
        {
            var id = person.Account.Id;
            var expected = owed.Contains(id) ? 1 : 0;
            var badge = await svc.GetOpenCountAsync(person.Principal);
            var shift = await svc.GetOpenForMeAsync(person.Principal);
            var list = (await svc.GetListAsync(person.Principal, repealed: false)).Count(r => r.MustAcknowledge);
            var listed = quota.Missing.Count(m => m.AgentId == id);

            Assert.True(badge == expected, $"{message} / {id}: badge {badge}, expected {expected}");
            Assert.True(shift.Count == expected, $"{message} / {id}: shift {shift.Count}, expected {expected}");
            Assert.True(list == expected, $"{message} / {id}: list {list}, expected {expected}");
            Assert.True(listed == expected, $"{message} / {id}: quota {listed}, expected {expected}");
        }
    }

    [Theory]
    [MemberData(nameof(Levels))]
    public async Task Badge_shift_list_and_quota_agree_for_every_level(DocumentClassification level)
    {
        using var ctx = new SqliteTestContext();
        var cast = Cast();
        await SeedAgentsAsync(ctx, [Seed.Agent("boss", Rank.Director), .. cast.Select(p => p.Account)]);
        var (svc, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: level), Boss());
        var audience = AudienceOf(level);
        var owed = OwedBy(level);

        await AssertAgreementAsync(svc, cast, id, audience, owed, $"{level} fresh");

        // everyone who may acknowledges, and the three numbers fall together
        foreach (var person in cast.Where(p => owed.Contains(p.Account.Id)))
        {
            await svc.AcknowledgeAsync(id, 1, person.Principal);
        }
        await AssertAgreementAsync(svc, cast, id, audience, [], $"{level} acknowledged");

        // a version that asks again puts the same agents back on the list
        await svc.PublishVersionAsync(id, Next(1, level), Boss());
        await AssertAgreementAsync(svc, cast, id, audience, owed, $"{level} asked again");

        // a correction asks nothing, even of the ones who never acknowledged
        await svc.AcknowledgeAsync(id, 2, cast.First(p => owed.Contains(p.Account.Id)).Principal);
        await svc.PublishVersionAsync(id, Next(2, level, requires: false), Boss());
        var stillOwed = owed.Where(o => o != cast.First(p => owed.Contains(p.Account.Id)).Account.Id).ToArray();
        await AssertAgreementAsync(svc, cast, id, audience, stillOwed, $"{level} corrected");
    }

    [Fact]
    public async Task Joining_or_gaining_the_level_later_creates_the_debt_and_losing_it_ends_it()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _) = Build(ctx);
        var open = await svc.CreateAsync(Input("Für alle"), Boss());
        var tru = await svc.CreateAsync(Input("Nur HRT", DocumentClassification.Tru), Boss());
        var lead = await svc.CreateAsync(Input("Nur Führung", DocumentClassification.Leadership), Boss());

        // a newcomer owes what already stands, without any snapshot
        await SeedAgentsAsync(ctx, Seed.Agent("newbie", Rank.Trainee));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("newbie")));
        Assert.Equal(open, Assert.Single(await svc.GetOpenForMeAsync(Plain("newbie"))).DirectiveId);

        // the level arrives
        await SetAgentAsync(ctx, "newbie", a => a.IsTRU = true);
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("newbie")));
        Assert.Equal(new[] { open, tru }.Order(), (await svc.GetOpenForMeAsync(Plain("newbie"))).Select(o => o.DirectiveId).Order());
        Assert.Contains((await svc.GetQuotaAsync(tru, Boss()))!.Missing, m => m.AgentId == "newbie");

        // a promotion opens the leadership ones
        await SetAgentAsync(ctx, "newbie", a => a.Rank = Rank.SectionChief);
        Assert.Equal(3, await svc.GetOpenCountAsync(Plain("newbie")));
        Assert.Contains((await svc.GetQuotaAsync(lead, Boss()))!.Missing, m => m.AgentId == "newbie");

        // losing the rank and the flag ends it again
        await SetAgentAsync(ctx, "newbie", a => { a.Rank = Rank.Trainee; a.IsTRU = false; });
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("newbie")));
        Assert.DoesNotContain((await svc.GetQuotaAsync(tru, Boss()))!.Missing, m => m.AgentId == "newbie");
        Assert.DoesNotContain((await svc.GetQuotaAsync(lead, Boss()))!.Missing, m => m.AgentId == "newbie");

        // an agent who leaves owes nothing
        await SetAgentAsync(ctx, "newbie", a => a.Status = AgentStatus.Terminated);
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("newbie")));
        Assert.DoesNotContain((await svc.GetQuotaAsync(open, Boss()))!.Missing, m => m.AgentId == "newbie");
    }

    [Fact]
    public async Task A_trashed_or_repealed_directive_is_owed_by_nobody()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var user = new FixedUser();
        var (svc, _) = Build(ctx, new AuditSaveChangesInterceptor(user));
        var first = await svc.CreateAsync(Input("Eins"), Boss());
        var second = await svc.CreateAsync(Input("Zwei"), Boss());
        var third = await svc.CreateAsync(Input("Drei"), Boss());
        Assert.Equal(3, await svc.GetOpenCountAsync(Plain("a")));

        await svc.RepealAsync(first, "überholt", Boss());
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));
        await svc.DeleteAsync(second, Boss());
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));

        Assert.Equal(third, Assert.Single(await svc.GetOpenForMeAsync(Plain("a"))).DirectiveId);
        await using var db = ctx.NewContext();
        Assert.Empty(await DirectiveDebt.Audience(db).Where(r => r.DirectiveId == first || r.DirectiveId == second)
            .ToListAsync());

        await svc.RestoreAsync(second, Boss());
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));
    }

    [Fact]
    public async Task The_open_list_is_oldest_first_and_carries_the_publication_of_the_asked_version()
    {
        using var ctx = new SqliteTestContext();
        var asked = Stamp.AddDays(2);
        var young = Stamp.AddDays(5);
        await using (var seed = ctx.NewContext())
        {
            seed.Users.Add(Seed.Agent("a", Rank.Trainee));
            // DA-01 asked in its second version, DA-02 only now, in its first
            seed.Directives.AddRange(
                new Directive { Id = "d1", Number = 1, Title = "Alt", CurrentVersion = 3, AcknowledgeFromVersion = 2, CreatedAt = Stamp },
                new Directive { Id = "d2", Number = 2, Title = "Jung", CurrentVersion = 1, AcknowledgeFromVersion = 1, CreatedAt = Stamp });
            seed.DirectiveVersions.AddRange(
                VersionRow("d1", 1, Stamp),
                VersionRow("d1", 2, asked),
                VersionRow("d1", 3, Stamp.AddDays(9)),
                VersionRow("d2", 1, young));
            // the first version of DA-01 is too old to count, the third would not be asked for
            seed.DirectiveAcknowledgments.Add(new DirectiveAcknowledgment
            {
                DirectiveId = "d1", Version = 1, AgentId = "a", AcknowledgedAt = Stamp, CreatedAt = Stamp,
            });
            await seed.SaveChangesAsync();
        }
        var (svc, _) = Build(ctx);

        var open = await svc.GetOpenForMeAsync(Plain("a"));

        Assert.Equal(["DA-01", "DA-02"], open.Select(o => o.Label));
        Assert.Equal([2, 1], open.Select(o => o.Version));
        Assert.Equal([asked, young], open.Select(o => o.Since));
        Assert.Equal(["Alt", "Jung"], open.Select(o => o.Title));
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));

        // order follows the dates
        await using (var db = ctx.NewContext())
        {
            (await db.DirectiveVersions.SingleAsync(v => v.DirectiveId == "d2")).PublishedAt = Stamp.AddDays(1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(["DA-02", "DA-01"], (await svc.GetOpenForMeAsync(Plain("a"))).Select(o => o.Label));
    }

    private static DirectiveVersion VersionRow(string directiveId, int version, DateTime publishedAt)
        => new()
        {
            DirectiveId = directiveId,
            Version = version,
            ContentHtml = "<p>Text</p>",
            ValidFrom = Today.AddDays(-30),
            ChangeNote = "Notiz",
            RequiresAcknowledgment = true,
            PublishedAt = publishedAt,
            CreatedAt = Stamp,
        };

    private static async Task SetAgentAsync(SqliteTestContext ctx, string id, Action<Agent> change)
    {
        await using var db = ctx.NewContext();
        change(await db.Users.SingleAsync(u => u.Id == id));
        await db.SaveChangesAsync();
    }

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("boss", "Falke", true, false, false);
    }
}
