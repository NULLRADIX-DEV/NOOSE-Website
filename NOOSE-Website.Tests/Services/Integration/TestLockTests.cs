using System.Security.Claims;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>A running internal test locks its pages: the rules, the lock read, search and FIBI.</summary>
public sealed class TestLockTests
{
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Agent(string id = "a1")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SpecialAgent).WithCodename($"Codename-{id}").Build();

    private static void SeedAttempt(SqliteTestContext ctx, string? blocked = "dienstverordnung", DateTime? startedAt = null,
        DateTime? completedAt = null, DateTime? deadlineAt = null, bool started = true)
    {
        using var db = ctx.NewContext();
        db.Users.AddRange(Seed.Agent("a1", Rank.SpecialAgent), Seed.Agent("a2", Rank.SpecialAgent));
        db.BewerbungTests.Add(new BewerbungTest
        {
            Id = "t1", Title = "DVO-Test", Usage = TestUsage.Intern, BlockedPages = blocked, CreatedAt = Ts,
        });
        db.AgentTestAssignments.Add(new AgentTestAssignment
        {
            Id = "z1", AgentId = "a1", TestId = "t1", CreatedAt = Ts,
            StartedAt = started ? startedAt ?? DateTime.UtcNow.AddMinutes(-5) : null,
            CompletedAt = completedAt, DeadlineAt = deadlineAt,
        });
        db.SaveChanges();
    }

    // ---- rules ----

    [Theory]
    [InlineData("/dienstverordnung", true)]
    [InlineData("/dienstverordnung/", true)]
    [InlineData("/dienstverordnung?tab=organisation", true)]
    [InlineData("/dienstverordnung/absatz/abc", true)]
    [InlineData("/Dienstverordnung/druck", true)]
    [InlineData("/dienstverordnungen", false)]
    [InlineData("/tests/z1", false)]
    [InlineData("/", false)]
    [InlineData("", false)]
    public void IsBlocked_MatchesTheRouteAndEverythingBelowIt(string path, bool blocked)
        => Assert.Equal(blocked, TestLockRules.IsBlocked(path, ["/dienstverordnung"]));

    [Fact]
    public void TheTestPageItselfCanNeverBeLocked()
    {
        Assert.DoesNotContain(TestLockRules.Lockable, e => e.Key == TestLockRules.TestsKey);
        Assert.Null(TestLockRules.Serialize(["tests"]));
        Assert.Empty(TestLockRules.Parse("tests"));
    }

    [Fact]
    public void Serialize_DropsUnknownKeysAndDuplicates_AndKeepsCatalogOrder()
    {
        var stored = TestLockRules.Serialize(["gesetze", "erfunden", "dienstverordnung", "gesetze"]);

        Assert.Equal(["gesetze", "dienstverordnung"], TestLockRules.Parse(stored));
        Assert.Null(TestLockRules.Serialize([]));
    }

    // ---- lock read ----

    [Fact]
    public async Task RunningAttempt_LocksItsPages()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx);

        var locked = await new TestLockService(ctx.Factory).GetActiveAsync(Agent());

        Assert.NotNull(locked);
        Assert.Equal("z1", locked.AssignmentId);
        Assert.Equal("DVO-Test", locked.TestTitle);
        Assert.True(locked.Blocks("/dienstverordnung/absatz/x"));
        Assert.False(locked.Blocks("/gesetze"));
    }

    [Fact]
    public async Task NotStartedYet_LocksNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, started: false);

        Assert.Null(await new TestLockService(ctx.Factory).GetActiveAsync(Agent()));
    }

    [Fact]
    public async Task HandedIn_LocksNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, completedAt: DateTime.UtcNow);

        Assert.Null(await new TestLockService(ctx.Factory).GetActiveAsync(Agent()));
    }

    [Fact]
    public async Task ClockRunOutPastTheGrace_LocksNothing_EvenBeforeTheSweepClosesIt()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, deadlineAt: DateTime.UtcNow - TestDeadline.SubmitGrace - TimeSpan.FromMinutes(1));

        Assert.Null(await new TestLockService(ctx.Factory).GetActiveAsync(Agent()));
    }

    [Fact]
    public async Task InsideTheGrace_StillLocks()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, deadlineAt: DateTime.UtcNow.AddSeconds(-30));

        Assert.NotNull(await new TestLockService(ctx.Factory).GetActiveAsync(Agent()));
    }

    [Fact]
    public async Task TestWithoutLockedPages_LocksNothing()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, blocked: null);

        Assert.Null(await new TestLockService(ctx.Factory).GetActiveAsync(Agent()));
    }

    [Fact]
    public async Task AnotherAgentsAttempt_DoesNotLockMe()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx);

        Assert.Null(await new TestLockService(ctx.Factory).GetActiveAsync(Agent("a2")));
    }

    // ---- consumers ----

    [Fact]
    public async Task Search_DropsTheCategoriesOfLockedPages()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx);
        var open = await SearchTestHost.NewService(ctx).SearchAsync(new SearchCriteria { Text = "Funk" }, Agent("a2"));

        var locked = await NewSearch(ctx).SearchAsync(new SearchCriteria { Text = "Funk" }, Agent());

        Assert.Equal(open.VisibleCategories - 1, locked.VisibleCategories);
        Assert.DoesNotContain(nameof(NOOSE_Website.Data.Entities.Regulation.RegulationParagraph), locked.Searched);
    }

    [Fact]
    public async Task Fibi_RefusesWhileAPageLockRuns()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx);
        var llm = Substitute.For<ILlmService>();
        var gateway = new NooseiGateway(llm, Substitute.For<ILlmQuotaService>(), NooseiProviderStub.Returning(),
            Options.Create(new LlmOptions()), NullLogger<NooseiGateway>.Instance, new TestLockService(ctx.Factory));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => gateway.AskAsync(
            new NooseiCall(LlmFeature.Chat, [LlmMessage.User("Was sagt die DVO zum Funk?")], LoggedPrompt: "x"), Agent()));

        Assert.Contains("DVO-Test", error.Message);
        await llm.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default);
    }

    private static SearchService NewSearch(SqliteTestContext ctx)
    {
        var plain = SearchTestHost.Providers(ctx);
        return new SearchService(plain, ctx.Factory, new SearchTestHost.UnrestrictedPartnerPolicy(),
            Options.Create(new SearchOptions { MaxConcurrency = 1 }), NullLogger<SearchService>.Instance,
            new TestLockService(ctx.Factory));
    }
}
