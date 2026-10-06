using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Statistics;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="SituationReportService"/> over in-memory SQLite.</summary>
public sealed class SituationReportServiceTests
{
    private static (SituationReportService Svc, IStatisticsService Statistics, INotificationService Notifications) Build(
        SqliteTestContext ctx, StatisticsReport? report = null)
    {
        var statistics = Substitute.For<IStatisticsService>();
        statistics.GetReportAsync(Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(report ?? SampleReport());
        var notifications = Substitute.For<INotificationService>();
        var financing = Substitute.For<IFinancingStatisticsService>();
        financing.GetMonthAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(FinancingReport.Empty);
        var svc = new SituationReportService(ctx.Factory, statistics, financing, notifications,
            NullLogger<SituationReportService>.Instance);
        return (svc, statistics, notifications);
    }

    private static StatisticsReport SampleReport(int people = 7) => new(
        new DashboardMetrics(people, 1, 2, 3, 4, 5, 6),
        Array.Empty<DistributionSegment>(),
        Array.Empty<DistributionSegment>(),
        Array.Empty<DistributionSegment>(),
        Array.Empty<DistributionSegment>(),
        Array.Empty<DistributionSegment>(),
        Array.Empty<DistributionSegment>(),
        Array.Empty<StatisticsTopEntry>(),
        Array.Empty<StatisticsTopEntry>(),
        Array.Empty<StatisticsMonth>());

    private static SituationReport Report(int year, int month, Action<SituationReport>? configure = null)
    {
        var r = new SituationReport
        {
            Year = year,
            Month = month,
            Title = $"Lagebericht {year}-{month:D2}",
            SnapshotJson = JsonSerializer.Serialize(SampleReport()),
        };
        configure?.Invoke(r);
        return r;
    }

    // ---------- GenerateMonthAsync ----------

    [Fact]
    public async Task GenerateMonthAsync_NoExisting_CreatesAndPersistsSnapshot()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SampleReport(people: 42));

        var bulletin = await svc.GenerateMonthAsync(2026, 3, replaceExisting: false, triggerId: "trigger-1");

        Assert.NotNull(bulletin);
        Assert.Equal(2026, bulletin!.Year);
        Assert.Equal(3, bulletin.Month);
        Assert.Equal("trigger-1", bulletin.CreatedById);
        Assert.Contains("2026", bulletin.Title);

        using var check = ctx.NewContext();
        var stored = Assert.Single(check.SituationReports.ToList());
        Assert.Equal("trigger-1", stored.CreatedById);
        Assert.False(string.IsNullOrEmpty(stored.SnapshotJson));
        // Snapshot round-trips back to the statistics report the service froze.
        var round = JsonSerializer.Deserialize<StatisticsReport>(stored.SnapshotJson,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        Assert.NotNull(round);
        Assert.Equal(42, round!.Metrics.People);
    }

    [Fact]
    public async Task GenerateMonthAsync_NotifiesActiveLeadershipOnly()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, notifications) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead1", Rank.SupervisorySpecialAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("lead2", Rank.Director, AgentStatus.Active));
            db.Users.Add(Seed.Agent("junior", Rank.SpecialAgent, AgentStatus.Active));      // below leadership
            db.Users.Add(Seed.Agent("inactive", Rank.Director, AgentStatus.Blocked));       // not active
            db.SaveChanges();
        }

        var bulletin = await svc.GenerateMonthAsync(2026, 5, replaceExisting: false, triggerId: "trigger-1");

        Assert.NotNull(bulletin);
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(c =>
                c.Count == 2 && c.Contains("lead1") && c.Contains("lead2")),
            NotificationType.SituationReport,
            Arg.Any<string>(),
            $"/lageberichte/{bulletin!.Id}",
            "trigger-1",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GenerateMonthAsync_ExistingNoReplace_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, statistics, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(Report(2026, 4));
            db.SaveChanges();
        }

        var result = await svc.GenerateMonthAsync(2026, 4, replaceExisting: false, triggerId: null);

        Assert.Null(result);
        // No snapshot recomputed, still exactly one row.
        await statistics.DidNotReceive().GetReportAsync(
            Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        using var check = ctx.NewContext();
        Assert.Single(check.SituationReports.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task GenerateMonthAsync_ExistingWithReplace_SoftDeletesOldAndCreatesNew()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        SituationReport old = Report(2026, 6);
        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(old);
            db.SaveChanges();
        }

        var bulletin = await svc.GenerateMonthAsync(2026, 6, replaceExisting: true, triggerId: "trigger-1");

        Assert.NotNull(bulletin);
        Assert.NotEqual(old.Id, bulletin!.Id);

        using var check = ctx.NewContext();
        // Normal (soft-delete-filtered) set: only the fresh report.
        var live = Assert.Single(check.SituationReports.ToList());
        Assert.Equal(bulletin.Id, live.Id);
        // The old one is explicitly soft-deleted by the service and stamped with the trigger.
        var deleted = check.SituationReports.IgnoreQueryFilters().Single(r => r.Id == old.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("trigger-1", deleted.DeletedById);
        Assert.NotNull(deleted.DeletedAt);
    }

    // ---------- GenerateDueAsync ----------

    [Fact]
    public async Task GenerateDueAsync_NoReport_CreatesPreviousMonth_ReturnsTrue()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var created = await svc.GenerateDueAsync(new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc));

        Assert.True(created);
        using var check = ctx.NewContext();
        var stored = Assert.Single(check.SituationReports.ToList());
        Assert.Equal(2026, stored.Year);
        Assert.Equal(6, stored.Month); // previous month
    }

    [Fact]
    public async Task GenerateDueAsync_AlreadyExists_ReturnsFalse()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(Report(2026, 6)); // previous month relative to July
            db.SaveChanges();
        }

        var created = await svc.GenerateDueAsync(new DateTime(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc));

        Assert.False(created);
        using var check = ctx.NewContext();
        Assert.Single(check.SituationReports.IgnoreQueryFilters().ToList());
    }

    // ---------- GetArchiveAsync ----------

    [Fact]
    public async Task GetArchiveAsync_OrdersNewestFirst_AndResolvesCreator()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator1"));
            db.SituationReports.Add(Report(2026, 3, r => r.CreatedById = "creator1")); // newest
            db.SituationReports.Add(Report(2026, 1, r => r.CreatedById = null));        // middle, no creator
            db.SituationReports.Add(Report(2025, 12, r => r.CreatedById = "creator1")); // oldest
            db.SaveChanges();
        }

        var archive = await svc.GetArchiveAsync();

        Assert.Equal(3, archive.Count);
        Assert.Equal((2026, 3), (archive[0].Year, archive[0].Month));
        Assert.Equal((2026, 1), (archive[1].Year, archive[1].Month));
        Assert.Equal((2025, 12), (archive[2].Year, archive[2].Month));
        Assert.Equal("Codename-creator1", archive[0].GeneratedBy);
        Assert.Null(archive[1].GeneratedBy);   // null creator id
        Assert.Equal("Codename-creator1", archive[2].GeneratedBy);
    }

    // ---------- GetDisplayAsync ----------

    [Fact]
    public async Task GetDisplayAsync_ValidSnapshot_ReturnsDisplayWithDeserializedReport()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var row = new SituationReport
        {
            Year = 2026,
            Month = 2,
            Title = "Lagebericht Februar 2026",
            SnapshotJson = JsonSerializer.Serialize(SampleReport(people: 9)),
            CreatedById = "creator1",
        };
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator1"));
            db.SituationReports.Add(row);
            db.SaveChanges();
        }

        var display = await svc.GetDisplayAsync(row.Id);

        Assert.NotNull(display);
        Assert.Equal(row.Id, display!.Id);
        Assert.Equal("Lagebericht Februar 2026", display.Title);
        Assert.Equal("Codename-creator1", display.GeneratedBy);
        Assert.Equal(9, display.Report.Metrics.People);
    }

    [Fact]
    public async Task GetDisplayAsync_UnknownId_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        Assert.Null(await svc.GetDisplayAsync("missing"));
    }

    [Fact]
    public async Task GetDisplayAsync_UnreadableSnapshot_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var row = Report(2026, 2, r => r.SnapshotJson = "this-is-not-json");
        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(row);
            db.SaveChanges();
        }

        // Deserialization throws JsonException -> swallowed -> null.
        Assert.Null(await svc.GetDisplayAsync(row.Id));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_ExistingReport_SoftDeletesAndStampsActor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var row = Report(2026, 2);
        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(row);
            db.SaveChanges();
        }

        await svc.DeleteAsync(row.Id, ClaimsPrincipalBuilder.Agent("actor-x").WithRank(Rank.Director).Build());

        using var check = ctx.NewContext();
        // Service sets IsDeleted explicitly -> filtered out of the normal set.
        Assert.False(check.SituationReports.Any(r => r.Id == row.Id));
        var deleted = check.SituationReports.IgnoreQueryFilters().Single(r => r.Id == row.Id);
        Assert.True(deleted.IsDeleted);
        Assert.Equal("actor-x", deleted.DeletedById);
        Assert.NotNull(deleted.DeletedAt);
    }

    [Fact]
    public async Task DeleteAsync_UnknownId_NoOp()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        // Missing row returns early without throwing.
        await svc.DeleteAsync("missing", ClaimsPrincipalBuilder.Agent("actor-x").WithRank(Rank.Director).Build());

        using var check = ctx.NewContext();
        Assert.Empty(check.SituationReports.IgnoreQueryFilters().ToList());
    }

    [Fact]
    public async Task DeleteAsync_RefusesAnAgentBelowLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.DeleteAsync("any", ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build()));
    }

    // ---------- partner view ----------

    private static ClaimsPrincipal Parlament()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.Parlament, PartnerRank.Member).Build();

    private static StatisticsReport RichReport() => SampleReport() with
    {
        MeasureOutcomes = [new DistributionSegment("Spritze", 3)],
        TopPeople = [new StatisticsTopEntry("Max", "NOOSE-P-1", "/personen/p1", 80, HazardLevel.High)],
        TopFactions =
        [
            new StatisticsTopEntry("Ballas", "NOOSE-F-1", "/fraktionen/f-open", 70, HazardLevel.High),
            new StatisticsTopEntry("Geheim", "NOOSE-F-2", "/fraktionen/f-hidden", 60, HazardLevel.Medium),
        ],
        TimeSeries = [new StatisticsMonth(2026, 1, "Jan", 5, 2)],
    };

    private static SituationReport RichRow() => Report(2026, 2, r =>
    {
        r.SnapshotJson = JsonSerializer.Serialize(RichReport() with { Metrics = new DashboardMetrics(99, 1, 2, 3, 4, 5, 6) });
        r.ReleasedSnapshotJson = JsonSerializer.Serialize(RichReport());
        r.FinancingJson = JsonSerializer.Serialize(FinancingReport.Empty);
        r.CreatedById = "creator1";
    });

    [Fact]
    public async Task GetDisplayForAsync_PartnerWithoutTheFunction_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetDisplayForAsync("any", Parlament()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetArchiveForAsync(Parlament()));
    }

    [Fact]
    public async Task GetDisplayForAsync_Partner_GetsTheRedactedReportWithoutFinancingOrAuthor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var row = RichRow();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator1"));
            db.SituationReports.Add(row);
            db.Factions.Add(Seed.Faction("f-open", "Ballas", f => f.IsBadFaction = true));
            db.Factions.Add(Seed.Faction("f-hidden", "Geheim"));
            db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.Parlament, Features = PartnerFeature.SituationReports });
            db.PartnerReleaseRules.Add(new PartnerReleaseRule { Agency = PartnerAgency.Parlament, EntityType = "Faction", Scope = PartnerRuleScope.BadFactions });
            db.SaveChanges();
        }

        var display = await svc.GetDisplayForAsync(row.Id, Parlament());

        Assert.NotNull(display);
        Assert.Null(display!.Financing);
        Assert.Null(display.GeneratedBy);
        Assert.Null(display.Report.MeasureOutcomes);
        Assert.Null(display.Report.TopPeople);
        Assert.Equal(new[] { "Ballas" }, display.Report.TopFactions.Select(f => f.Name));
        Assert.All(display.Report.TimeSeries, m => Assert.Equal(0, m.Measures));
        Assert.Equal(0, display.Report.Metrics.OpenRequests);
        Assert.Equal(7, display.Report.Metrics.People);
        Assert.Null((await svc.GetArchiveForAsync(Parlament())).Single().GeneratedBy);
    }

    [Fact]
    public async Task Partner_NeverGetsAReportWithoutAReleasedSnapshot()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var old = Report(2026, 1);
        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(old);
            db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = PartnerAgency.Parlament, Features = PartnerFeature.SituationReports });
            db.SaveChanges();
        }

        Assert.Null(await svc.GetDisplayForAsync(old.Id, Parlament()));
        Assert.Empty(await svc.GetArchiveForAsync(Parlament()));
    }

    [Fact]
    public async Task GenerateMonthAsync_StoresAReleasedSnapshotWithoutClassifiedRecords()
    {
        using var ctx = new SqliteTestContext();
        var (svc, statistics, _) = Build(ctx);

        var row = await svc.GenerateMonthAsync(2026, 3, replaceExisting: false, triggerId: null);

        Assert.NotNull(row!.ReleasedSnapshotJson);
        await statistics.Received(1).GetReportAsync(Arg.Is(false), Arg.Is<string?>(m => m == null), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetDisplayForAsync_Leadership_GetsTheFullReport()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var row = RichRow();
        using (var db = ctx.NewContext())
        {
            db.SituationReports.Add(row);
            db.SaveChanges();
        }

        var display = await svc.GetDisplayForAsync(row.Id, ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build());

        Assert.Equal(2, display!.Report.TopFactions.Count);
        Assert.Single(display.Report.TopPeople);
        Assert.NotNull(display.Financing);
    }

    [Fact]
    public async Task GetDisplayForAsync_AgentBelowLeadership_IsRefused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            svc.GetDisplayForAsync("any", ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build()));
    }
}
