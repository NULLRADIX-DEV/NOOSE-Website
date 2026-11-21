using NOOSE_Website.Data.Entities.Usage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Services.Usage;

namespace NOOSE_Website.Tests.Services.Usage;

public sealed class UsageStatisticsServiceTests
{
    private static readonly StatisticsScope Scope = new(false, StatisticsRange.Days30);

    private static ClaimsPrincipalBuilder Admin() => ClaimsPrincipalBuilder.Agent("ad1").WithRank(Rank.Trainee).AsAdmin();

    private static ClaimsPrincipalBuilder Leader() => ClaimsPrincipalBuilder.Agent("l1").WithRank(Rank.SectionChief);

    private static DateOnly Today => UsageCollector.DayOf(DateTime.UtcNow);

    [Fact]
    public async Task Only_an_admin_may_read_leadership_and_supervision_may_not()
    {
        using var ctx = new SqliteTestContext();
        var service = new UsageStatisticsService(ctx.Factory);

        await service.GetAsync(Admin(), Scope);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(Leader(), Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAsync(ClaimsPrincipalBuilder.Agent("s1").WithRank(Rank.Trainee).AsTeamLead(), Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAsync(ClaimsPrincipalBuilder.Agent("o1").WithRank(Rank.Director), Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAsync(ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.SpecialAgent), Scope));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetAsync(ClaimsPrincipalBuilder.Agent("p1").AsPartner(PartnerAgency.DoJ, PartnerRank.Chief), Scope));
    }

    [Fact]
    public async Task Every_catalog_feature_is_listed_and_an_unused_one_has_zeros()
    {
        using var ctx = new SqliteTestContext();
        var data = await new UsageStatisticsService(ctx.Factory).GetAsync(Admin(), Scope);

        Assert.Equal(UsageCatalog.All.Count, data.Rows.Count);
        Assert.All(data.Rows, r => Assert.True(r.Never && r.AgentDays == 0));
        Assert.Null(data.FirstDay);
    }

    [Fact]
    public async Task Rows_inside_the_window_are_summed_and_older_ones_are_left_out()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.UsageDays.AddRange(
                new UsageDay { Day = Today, Key = UsageCatalog.SearchRun, Events = 5, Agents = 3 },
                new UsageDay { Day = Today.AddDays(-2), Key = UsageCatalog.SearchRun, Events = 2, Agents = 2 },
                new UsageDay { Day = Today.AddDays(-100), Key = UsageCatalog.SearchRun, Events = 40, Agents = 9 },
                // a key the catalog no longer knows is not shown
                new UsageDay { Day = Today, Key = "page:retired", Events = 7, Agents = 4 });
            db.SaveChanges();
        }

        var data = await new UsageStatisticsService(ctx.Factory).GetAsync(Admin(), Scope);

        var row = data.Rows.Single(r => r.Key == UsageCatalog.SearchRun);
        Assert.Equal((7L, 5, 3), (row.Events, row.AgentDays, row.PeakAgents));
        Assert.DoesNotContain(data.Rows, r => r.Key == "page:retired");
        Assert.Equal(Today.AddDays(-100), data.FirstDay);
    }

    [Fact]
    public async Task A_range_longer_than_the_retention_is_cut_and_says_so()
    {
        using var ctx = new SqliteTestContext();
        var data = await new UsageStatisticsService(ctx.Factory)
            .GetAsync(Admin(), new StatisticsScope(false, StatisticsRange.Months24));

        Assert.True(data.ClippedByRetention);
        Assert.Equal(Today.AddDays(-(UsageRules.RetentionDays - 1)), data.From);
    }
}
