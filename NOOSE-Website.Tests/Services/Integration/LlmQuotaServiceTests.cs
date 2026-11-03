using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LlmQuotaService"/>: rank base, override, the weekly carry chain and its cap.</summary>
public sealed class LlmQuotaServiceTests
{
    private const string AgentId = "agent-ki";

    private static ClaimsPrincipal Owner()
        => ClaimsPrincipalBuilder.Agent("owner").WithRank(Rank.Director).AsAiOwner().Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static LlmQuotaService Build(SqliteTestContext ctx, LlmQuotaConfig? config = null, int boostPercent = 0)
    {
        var configService = Substitute.For<ILlmQuotaConfigService>();
        configService.GetAsync(Arg.Any<CancellationToken>()).Returns(config ?? LlmQuotaConfig.Default());
        return new LlmQuotaService(ctx.Factory, configService, NooseiProviderStub.Returning(boostPercent: boostPercent),
            Options.Create(new LlmOptions()), NullLogger<LlmQuotaService>.Instance);
    }

    private static LlmQuotaConfig Config(Rank rank, long baseWeekly, int carryPercent) => new()
    {
        Ranks = new Dictionary<string, LlmRankQuota>
        {
            [LlmQuotaConfig.RankKey(rank)] = new() { BaseWeekly = baseWeekly, CarryOverPercent = carryPercent },
        },
    };

    private static async Task SeedAgentAsync(SqliteTestContext ctx, Rank? rank = Rank.SpecialAgent, long? over = null)
    {
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(AgentId, rank ?? Rank.SpecialAgent, configure: a =>
        {
            a.Rank = rank;
            a.LlmQuotaOverride = over;
        }));
        await db.SaveChangesAsync();
    }

    /// <param name="at">When it was charged. Only the week matters to the ledger, but the daily ceiling counts from
    /// local midnight — so a week's worth of tokens booked on one day is a different scenario than the same week
    /// spread over it.</param>
    private static async Task ChargeAsync(SqliteTestContext ctx, int year, int week, long tokens, DateTime? at = null)
    {
        await using var db = ctx.NewContext();
        db.LlmRequests.Add(new LlmRequestLog
        {
            AgentId = AgentId,
            CreatedAt = at ?? DateTime.UtcNow,
            BudgetYear = year,
            BudgetWeek = week,
            Feature = LlmFeature.Chat,
            QuotaTokens = tokens,
            Success = true,
        });
        await db.SaveChangesAsync();
    }

    private static async Task ClosePeriodAsync(SqliteTestContext ctx, int year, int week, long carryOut, long baseWeekly = 35_000, int percent = 0)
    {
        await using var db = ctx.NewContext();
        db.LlmQuotaPeriods.Add(new LlmQuotaPeriod
        {
            AgentId = AgentId,
            Year = year,
            Week = week,
            BaseWeekly = baseWeekly,
            CarryOut = carryOut,
            CarryPercent = percent,
            ClosedAt = DateTime.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static (int Year, int Week) Now() => IsoWeekPeriod.Current();

    private static (int Year, int Week) LastWeek()
    {
        var (year, week) = IsoWeekPeriod.Current();
        return IsoWeekPeriod.Previous(year, week);
    }

    // ---- base, rank and override ----

    [Fact]
    public async Task BaseWeekly_ComesFromTheRank()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(50_000L, status.BaseWeekly);
        Assert.Equal(25, status.CarryPercent);
        Assert.False(status.IsOverride);
    }

    [Fact]
    public async Task TheActiveUpstreamsBoost_LiftsTheWeeklyBase()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);

        var status = await Build(ctx, boostPercent: 100).GetStatusAsync(AgentId, Leader());

        Assert.Equal(100_000L, status.BaseWeekly);
        // derived from the base, so both move with it — that is why the boost is applied there
        Assert.Equal(25_000L, status.CarryCap);
        Assert.Equal(LlmQuotaMath.DailyLimit(100_000, LlmRankQuota.DefaultDailyPercent), status.DailyLimit);
    }

    /// <summary>An individual allowance says who may spend how much; the boost says what the endpoint costs.
    /// They are different questions, so the boost rides on top of an override too.</summary>
    [Fact]
    public async Task TheBoost_AlsoLiftsAnIndividualOverride()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee, over: 10_000);

        var status = await Build(ctx, boostPercent: 50).GetStatusAsync(AgentId, Leader());

        Assert.Equal(15_000L, status.BaseWeekly);
        Assert.Equal(10_000L, status.RawBaseWeekly);
        Assert.Equal(50, status.BoostPercent);
        Assert.True(status.IsOverride);
    }

    /// <summary>The editor writes back what it was shown. It is shown RawBaseWeekly, so a confirm that changes
    /// nothing must change nothing — handing back the boosted figure would apply the surcharge a second time,
    /// and again on every further confirm.</summary>
    [Fact]
    public async Task WritingBackTheShownBase_DoesNotApplyTheBoostTwice()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee, over: 10_000);

        var before = await Build(ctx, boostPercent: 400).GetStatusAsync(AgentId, Leader());
        // exactly what LlmQuotaOverrideDialog puts in its field and hands to SetOverrideAsync
        await Build(ctx, boostPercent: 400).SetOverrideAsync(AgentId, before.RawBaseWeekly, Owner());
        var after = await Build(ctx, boostPercent: 400).GetStatusAsync(AgentId, Leader());

        Assert.Equal(before.RawBaseWeekly, after.RawBaseWeekly);
        Assert.Equal(before.BaseWeekly, after.BaseWeekly);
        Assert.Equal(50_000L, after.BaseWeekly);
    }

    [Fact]
    public async Task UnrankedAgent_GetsNothing()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, rank: null);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(0L, status.BaseWeekly);
        Assert.True(status.IsBlocked);
    }

    [Fact]
    public async Task Override_BeatsTheRankDefault()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee, over: 123_000);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(123_000L, status.BaseWeekly);
        Assert.True(status.IsOverride);
    }

    // ---- consumption ----

    [Fact]
    public async Task Consumed_SumsThisWeek_AndIgnoresOtherWeeks()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var (year, week) = Now();
        var (lastYear, lastWeek) = LastWeek();
        await ChargeAsync(ctx, year, week, 4_000);
        await ChargeAsync(ctx, year, week, 1_000);
        await ChargeAsync(ctx, lastYear, lastWeek, 9_000);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(5_000L, status.Consumed);
    }

    [Fact]
    public async Task Consumed_IsReducedByATopUp_AndRaisedByADeduction()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 10_000);
        var svc = Build(ctx);

        await svc.TopUpAsync(AgentId, 4_000, "Ausgleich", Owner());
        Assert.Equal(6_000L, (await svc.GetStatusAsync(AgentId, Leader())).Consumed);

        await svc.TopUpAsync(AgentId, -1_000, "Abzug", Owner());
        Assert.Equal(7_000L, (await svc.GetStatusAsync(AgentId, Leader())).Consumed);
    }

    [Fact]
    public async Task Remaining_MayGoNegative_AfterAnExpensiveAnswer()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 25_000);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(-5_000L, status.Remaining);
        Assert.True(status.IsBlocked);
    }

    // ---- carry chain ----

    [Fact]
    public async Task CarryIn_ComesFromTheDirectPredecessorWeek()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ClosePeriodAsync(ctx, lastYear, lastWeek, carryOut: 7_500, baseWeekly: 50_000, percent: 25);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(7_500L, status.CarryIn);
        Assert.Equal(57_500L, status.Available);
    }

    [Fact]
    public async Task CarryIn_IsClampedOnRead_WhenTheRuleShrankAfterTheClose()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ClosePeriodAsync(ctx, lastYear, lastWeek, carryOut: 40_000);

        // the rank now allows only 25 % of 20.000 = 5.000, and the frozen 40.000 must not survive that
        var status = await Build(ctx, Config(Rank.SpecialAgent, 20_000, 25)).GetStatusAsync(AgentId, Leader());

        Assert.Equal(5_000L, status.CarryIn);
    }

    /// <summary>A week closes lazily, on the first read of the week that follows it. Whatever surcharge is in
    /// force at that moment has nothing to do with the week being closed — feeding it in let a week spent on the
    /// cheaper upstream hand over a multiple of the carry it had earned.</summary>
    [Fact]
    public async Task AnElapsedWeek_IsNotClosedWithTodaysBoost()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ChargeAsync(ctx, lastYear, lastWeek, 10_000);

        // the whole of last week ran without a surcharge; the owner switches upstream before anyone looks
        var status = await Build(ctx, boostPercent: 400).GetStatusAsync(AgentId, Leader());

        // 25 % of min(50.000 - 10.000, 50.000) = 10.000 — the boosted base would have made it 60.000
        Assert.Equal(10_000L, status.CarryIn);
    }

    [Fact]
    public async Task NoHistory_MeansNoPhantomCarryOver_AndNoRowsWritten()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Director);

        var status = await Build(ctx).GetStatusAsync(AgentId, Leader());

        Assert.Equal(0L, status.CarryIn);
        await using var db = ctx.NewContext();
        Assert.Empty(await db.LlmQuotaPeriods.ToListAsync());
    }

    [Fact]
    public async Task ElapsedWeek_IsClosedOnce_AndCarriesItsRestForward()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ChargeAsync(ctx, lastYear, lastWeek, 30_000);
        var svc = Build(ctx);

        var first = await svc.GetStatusAsync(AgentId, Leader());
        var second = await svc.GetStatusAsync(AgentId, Leader());

        // rest 20.000 of 50.000 at 25 % → 5.000
        Assert.Equal(5_000L, first.CarryIn);
        Assert.Equal(first.CarryIn, second.CarryIn);
        await using var db = ctx.NewContext();
        var period = Assert.Single(await db.LlmQuotaPeriods.ToListAsync());
        Assert.Equal(30_000L, period.Consumed);
        Assert.Equal(5_000L, period.CarryOut);
    }

    [Fact]
    public async Task ClosedWeek_StaysFrozen_WhenTheRuleChangesLater()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ChargeAsync(ctx, lastYear, lastWeek, 10_000);
        await Build(ctx).GetStatusAsync(AgentId, Leader());

        await Build(ctx, Config(Rank.SeniorSpecialAgent, 1_000, 100)).GetStatusAsync(AgentId, Leader());

        await using var db = ctx.NewContext();
        var period = Assert.Single(await db.LlmQuotaPeriods.ToListAsync());
        Assert.Equal(50_000L, period.BaseWeekly);
    }

    // ---- pre-flight ----

    [Fact]
    public async Task EnsureAvailable_PassesAtExactlyOneTokenLeft()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        // earlier in the week, not today: almost a full weekly quota inside one day is what the daily ceiling stops
        await ChargeAsync(ctx, year, week, 19_999, DateTime.UtcNow.AddDays(-2));

        var status = await Build(ctx).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build());

        Assert.Equal(1L, status.Remaining);
    }

    [Fact]
    public async Task EnsureAvailable_StopsAtTheDailyCeiling_WhileTheWeekStillHasTokens()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        // 40 % of a 20.000 base is 8.000 for the day; the week is barely touched
        await ChargeAsync(ctx, year, week, 8_000);

        var ex = await Assert.ThrowsAsync<LlmQuotaExceededException>(() => Build(ctx).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build()));

        // it must not read as "your week is gone" — 12.000 tokens are still there tomorrow
        Assert.Contains("heute", ex.Message);
        Assert.DoesNotContain("aufgebraucht", ex.Message);
    }

    [Fact]
    public async Task DailyCeiling_CountsOnlyToday()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 8_000, DateTime.UtcNow.AddDays(-1));

        var status = await Build(ctx).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build());

        Assert.Equal(0L, status.ConsumedToday);
        Assert.False(status.IsDayBlocked);
        Assert.Equal(12_000L, status.Remaining);
    }

    [Fact]
    public async Task DailyCeiling_IsOffAtZeroPercent()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 19_000);
        var config = Config(Rank.Trainee, 20_000, 0);
        config.Ranks[LlmQuotaConfig.RankKey(Rank.Trainee)].DailyPercent = 0;

        var status = await Build(ctx, config).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build());

        Assert.Equal(0L, status.DailyLimit);
        Assert.False(status.IsDayBlocked);
    }

    [Fact]
    public async Task ExhaustedWeek_OutranksTheDailyCeiling()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 20_000);

        var ex = await Assert.ThrowsAsync<LlmQuotaExceededException>(() => Build(ctx).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build()));

        // both ceilings are crossed; "come back tomorrow" would be a lie when the week is gone until Monday
        Assert.Contains("aufgebraucht", ex.Message);
    }

    [Fact]
    public async Task EnsureAvailable_ThrowsWhenExhausted_AndNamesTheResetTime()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Trainee);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 20_000);

        var ex = await Assert.ThrowsAsync<LlmQuotaExceededException>(() => Build(ctx).EnsureAvailableAsync(
            ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build()));

        Assert.Contains("aufgebraucht", ex.Message);
        Assert.Contains("KW", ex.Message);
    }

    [Theory]
    [InlineData("partner")]
    [InlineData("demo")]
    [InlineData("supervision")]
    public async Task EnsureAvailable_DeniesTheRolesWithoutNooseiAccess(string role)
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var builder = ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.SpecialAgent);
        var actor = role switch
        {
            "partner" => builder.AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build(),
            "demo" => builder.AsDemo().Build(),
            _ => builder.AsTeamLead().Build(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).EnsureAvailableAsync(actor));
    }

    // ---- charging ----

    [Fact]
    public async Task TryCharge_WritesTheLogRow_AndReducesRemaining()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SpecialAgent);
        var svc = Build(ctx);

        var charge = await svc.TryChargeAsync(new LlmChargeInput(
            AgentId, LlmFeature.Chat, new LlmUsage(120, 40, 160, 10, 0, 0.0123m),
            "vendor/model", "Baidu", 850, 2, true, null,
            "Wer führt die Ballas?", "Die Aktenlage nennt …",
            [new LlmContextRef("Faction", "f1", "Ballas")]));

        Assert.Equal(1_230L, charge.QuotaTokens);
        Assert.True(charge.Persisted);
        Assert.Equal(35_000L - 1_230L, charge.Status.Remaining);

        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal("Wer führt die Ballas?", row.Prompt);
        Assert.Equal("Die Aktenlage nennt …", row.Answer);
        Assert.Contains("Ballas", row.ContextRefsJson);
        Assert.Equal(2, row.ToolRounds);
        Assert.NotNull(row.PromptFingerprint);
    }

    [Fact]
    public async Task TryCharge_StampsTheRunningIsoWeek()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);

        await Build(ctx).TryChargeAsync(new LlmChargeInput(
            AgentId, LlmFeature.Brief, new LlmUsage(1, 1, 2, 0, 0, 0.001m),
            null, null, 10, 0, true, null, "x", "y", null));

        var (year, week) = Now();
        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.Equal((year, week), (row.BudgetYear, row.BudgetWeek));
    }

    [Fact]
    public async Task TryCharge_FlagsACostSpike_OnceThereIsEnoughBaseline()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Director);
        var (year, week) = Now();
        for (var i = 0; i < 25; i++)
        {
            await ChargeAsync(ctx, year, week, 1_000);
        }
        var svc = Build(ctx);

        var normal = await svc.TryChargeAsync(Charge(0.01m));   // 1.000 Token, wie der Schnitt
        var spike = await svc.TryChargeAsync(Charge(0.30m));    // 30.000 Token, 30-fach

        Assert.Null(normal.Anomaly);
        Assert.Equal(LlmAnomalyKind.CostSpike, spike.Anomaly);

        await using var db = ctx.NewContext();
        var flagged = await db.LlmRequests.Where(r => r.IsAnomalous).ToListAsync();
        Assert.Single(flagged);
    }

    [Fact]
    public async Task TryCharge_DoesNotFlagWithoutEnoughHistory()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.Director);

        // an agent's second-ever request must never count as an outlier
        var charge = await Build(ctx).TryChargeAsync(Charge(0.50m));

        Assert.Null(charge.Anomaly);
    }

    private static LlmChargeInput Charge(decimal costUsd)
        => new(AgentId, LlmFeature.Chat, new LlmUsage(100, 20, 120, 0, 0, costUsd),
            "vendor/model", "Baidu", 500, 0, true, null, "Frage", "Antwort", null);

    [Fact]
    public async Task TryCharge_LogsAFailedCall_WithoutAnAnswer()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);

        var charge = await Build(ctx).TryChargeAsync(new LlmChargeInput(
            AgentId, LlmFeature.Chat, LlmUsage.Empty, null, null, 500, 0, false,
            "FIBI antwortete nicht (Fehler 502).", "Frage", null, null));

        Assert.Equal(0L, charge.QuotaTokens);
        await using var db = ctx.NewContext();
        var row = Assert.Single(await db.LlmRequests.ToListAsync());
        Assert.False(row.Success);
        Assert.Null(row.Answer);
        Assert.Contains("502", row.ErrorMessage);
    }

    // ---- owner-only corrections ----

    [Fact]
    public async Task SetOverride_IsAuditedAgainstThePersonnelRecord()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);

        await Build(ctx).SetOverrideAsync(AgentId, 99_000, Owner());

        await using var db = ctx.NewContext();
        Assert.Equal(99_000L, (await db.Users.FirstAsync(a => a.Id == AgentId)).LlmQuotaOverride);
        var audit = await db.AuditLogs.FirstAsync(a => a.EntityId == AgentId);
        Assert.Equal("Agent", audit.EntityType);
        Assert.Contains("KI-Kontingent", audit.ChangesJson);
    }

    [Fact]
    public async Task SetOverride_RejectsNegativeAmounts()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Build(ctx).SetOverrideAsync(AgentId, -1, Owner()));
    }

    [Theory]
    [InlineData("override")]
    [InlineData("topup")]
    [InlineData("reset")]
    public async Task Corrections_AreRefusedForEveryoneButTheAiOwner(string action)
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx);
        var admin = ClaimsPrincipalBuilder.Agent("adm").WithRank(Rank.Director).AsAdmin().AsBootstrap().Build();

        Task Act(ClaimsPrincipal actor) => action switch
        {
            "override" => svc.SetOverrideAsync(AgentId, 1_000, actor),
            "topup" => svc.TopUpAsync(AgentId, 1_000, "Grund", actor),
            _ => svc.ResetAsync(AgentId, "Grund", actor),
        };

        // leadership and even a bootstrap admin may look, never change
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Act(Leader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Act(admin));
    }

    [Fact]
    public async Task TopUp_RejectsZeroTokensAndAnEmptyReason()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TopUpAsync(AgentId, 0, "Grund", Owner()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.TopUpAsync(AgentId, 100, "  ", Owner()));
    }

    [Fact]
    public async Task Reset_ZeroesTheRunningWeek_ButLeavesTheCarryIn()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx, Rank.SeniorSpecialAgent);
        var (lastYear, lastWeek) = LastWeek();
        await ClosePeriodAsync(ctx, lastYear, lastWeek, carryOut: 7_500, baseWeekly: 50_000, percent: 25);
        var (year, week) = Now();
        await ChargeAsync(ctx, year, week, 12_000);
        var svc = Build(ctx);

        await svc.ResetAsync(AgentId, "Testlauf", Owner());
        var status = await svc.GetStatusAsync(AgentId, Leader());

        Assert.Equal(0L, status.Consumed);
        Assert.Equal(7_500L, status.CarryIn);
    }

    // ---- roster ----

    [Fact]
    public async Task GetAllStatus_ListsInternalAgentsIncludingAdmins_ButNoTeamLeadsOrPartners()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("plain", Rank.SpecialAgent, configure: a => a.Codename = "A-Plain"));
            db.Users.Add(Seed.Agent("admin", Rank.Director, configure: a =>
            {
                a.IsAdmin = true;
                a.Codename = "B-Admin";
            }));
            db.Users.Add(Seed.Agent("tl", Rank.Director, configure: a =>
            {
                a.IsTeamLead = true;
                a.IsAdmin = true;
                a.Codename = "C-TeamLead";
            }));
            db.Users.Add(Seed.Agent("partner", Rank.SpecialAgent, configure: a =>
            {
                a.PartnerAgency = PartnerAgency.LSPD;
                a.Codename = "D-Partner";
            }));
            await db.SaveChangesAsync();
        }

        var all = await Build(ctx).GetAllStatusAsync(Leader());

        Assert.Equal(["A-Plain", "B-Admin"], all.Select(s => s.Codename ?? string.Empty).ToArray());
    }

    [Fact]
    public async Task TheRosterAndTheSingleAgentPath_AgreeOnEveryNumber()
    {
        using var ctx = new SqliteTestContext();
        var (year, week) = Now();
        var (priorYear, priorWeek) = IsoWeekPeriod.Previous(year, week);
        await using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a-one", Rank.SeniorSpecialAgent, configure: a =>
            {
                a.Rank = Rank.SeniorSpecialAgent;
                a.Codename = "A-Eins";
            }));
            db.Users.Add(Seed.Agent("a-two", Rank.Director, configure: a =>
            {
                a.Rank = Rank.Director;
                a.Codename = "B-Zwei";
                a.LlmQuotaOverride = 90_000;
            }));
            // an unclosed prior week for one of them, so the roster has to replay a carry chain, not just read sums
            db.LlmRequests.Add(new LlmRequestLog
            {
                AgentId = "a-one", CreatedAt = DateTime.UtcNow.AddDays(-9), BudgetYear = priorYear,
                BudgetWeek = priorWeek, Feature = LlmFeature.Chat, QuotaTokens = 10_000, Success = true,
            });
            db.LlmRequests.Add(new LlmRequestLog
            {
                AgentId = "a-two", CreatedAt = DateTime.UtcNow, BudgetYear = year, BudgetWeek = week,
                Feature = LlmFeature.Chat, QuotaTokens = 4_000, Success = true,
            });
            db.LlmQuotaAdjustments.Add(new LlmQuotaAdjustment
            {
                AgentId = "a-two", Year = year, Week = week, Tokens = 1_000, Reason = "Korrektur",
                CreatedAt = DateTime.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var svc = Build(ctx);
        var roster = await svc.GetAllStatusAsync(Leader());
        // the roster reads four grouped queries instead of seven per agent; the numbers may not move because of it
        foreach (var row in roster)
        {
            var single = await svc.GetStatusAsync(row.AgentId, Leader());
            Assert.Equal(single.BaseWeekly, row.BaseWeekly);
            Assert.Equal(single.CarryIn, row.CarryIn);
            Assert.Equal(single.Consumed, row.Consumed);
            Assert.Equal(single.ConsumedToday, row.ConsumedToday);
            Assert.Equal(single.DailyLimit, row.DailyLimit);
        }
        Assert.Equal(2, roster.Count);
        Assert.Equal(3_000L, roster.Single(r => r.AgentId == "a-two").Consumed);
    }

    // ---- who may read a quota ----

    [Fact]
    public async Task Status_IsReadableByTheAgentThemselves()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var self = ClaimsPrincipalBuilder.Agent(AgentId).WithRank(Rank.Trainee).Build();

        var status = await Build(ctx).GetStatusAsync(AgentId, self);

        Assert.Equal(AgentId, status.AgentId);
    }

    [Fact]
    public async Task Status_IsRefusedToAnotherAgentWithoutTheClassifiedScope()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentAsync(ctx);
        var other = ClaimsPrincipalBuilder.Agent("someone-else").WithRank(Rank.SeniorSpecialAgent).Build();
        var svc = Build(ctx);

        // the panels guard this too, but the numbers must not be one SignalR call away from anyone
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetStatusAsync(AgentId, other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetPeriodsAsync(AgentId, other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAdjustmentsAsync(AgentId, other));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAllStatusAsync(other));
    }
}
