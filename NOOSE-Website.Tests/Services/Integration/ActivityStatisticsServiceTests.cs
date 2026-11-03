using System.Security.Claims;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="ActivityStatisticsService"/> over in-memory SQLite.</summary>
public sealed class ActivityStatisticsServiceTests
{
    private static ActivityStatisticsService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge).Build();

    private static ClaimsPrincipal RankAndFile()
        => ClaimsPrincipalBuilder.Agent("agent-2").WithRank(Rank.SpecialAgent).Build();

    private static StatisticsScope Scope(StatisticsRange range = StatisticsRange.Months12)
        => new(true, range);

    private static AuditLog Entry(DateTime timestampUtc, string entityType = "Person")
        => new()
        {
            Timestamp = timestampUtc,
            EntityType = entityType,
            EntityId = Guid.NewGuid().ToString(),
            Action = AuditAction.Modified,
            AgentId = "agent-1",
            AgentName = "Codename-agent-1",
        };

    [Fact]
    public async Task GetWeekdayHourAsync_NonLeadership_IsRejected()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // the audit log is leadership-gated everywhere else; aggregates must not be a side door
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetWeekdayHourAsync(RankAndFile(), Scope()));
    }

    [Fact]
    public async Task GetDailyDensityAsync_NonLeadership_IsRejected()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetDailyDensityAsync(RankAndFile(), Scope()));
    }

    [Fact]
    public async Task GetByRecordTypeAsync_NonLeadership_IsRejected()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetByRecordTypeAsync(RankAndFile(), Scope()));
    }

    [Fact]
    public async Task GetCaptureGapsAsync_NonLeadership_IsRejected()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetCaptureGapsAsync(RankAndFile(), Scope()));
    }

    [Fact]
    public async Task GetWeekdayHourAsync_GroupsHourOfDayInSql_AndKeepsTheFullGrid()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(DateTime.UtcNow.AddDays(-1)));
            db.SaveChanges();
        }

        // proves the Year/Month/Day/Hour GroupBy translates on a relational provider,
        // the one shape in this service that is not already used elsewhere in the codebase
        var matrix = await svc.GetWeekdayHourAsync(Leader(), Scope());

        Assert.Equal(7, matrix.Rows.Count);
        Assert.Equal(24, matrix.Columns.Count);
        Assert.Equal(7, matrix.Cells.Count);
        Assert.All(matrix.Cells, row => Assert.Equal(24, row.Count));
        Assert.Equal(1, matrix.Cells.SelectMany(c => c).Sum());
    }

    [Fact]
    public async Task GetWeekdayHourAsync_PlacesEntryOnItsLocalWeekdayAndHour()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var utc = DateTime.UtcNow.AddDays(-2);
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(utc));
            db.SaveChanges();
        }

        var matrix = await svc.GetWeekdayHourAsync(Leader(), Scope());

        // the app pins TZ=Europe/Berlin, so the displayed slot is local, not UTC
        var local = utc.ToLocalTime();
        var expectedRow = ((int)local.DayOfWeek + 6) % 7;
        Assert.Equal(1, matrix.Cells[expectedRow][local.Hour]);
    }

    [Fact]
    public async Task GetWeekdayHourAsync_EntriesOutsideTheWindow_AreIgnored()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(DateTime.UtcNow.AddYears(-3)));
            db.SaveChanges();
        }

        var matrix = await svc.GetWeekdayHourAsync(Leader(), Scope(StatisticsRange.Days30));

        Assert.Equal(0, matrix.Max);
        Assert.True(matrix.IsEmpty);
    }

    [Fact]
    public async Task GetWeekdayHourAsync_EmptyLog_StillReturnsTheFullGrid()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var matrix = await svc.GetWeekdayHourAsync(Leader(), Scope());

        Assert.Equal(7, matrix.Rows.Count);
        Assert.Equal(24, matrix.Columns.Count);
        Assert.True(matrix.IsEmpty);
    }

    [Fact]
    public async Task GetDailyDensityAsync_AggregatesPerLocalDay()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var day = DateTime.UtcNow.AddDays(-3).Date.AddHours(12);
        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(day));
            db.AuditLogs.Add(Entry(day.AddHours(1)));
            db.SaveChanges();
        }

        var days = await svc.GetDailyDensityAsync(Leader(), Scope());

        Assert.Single(days);
        Assert.Equal(2, days[0].Count);
    }

    [Fact]
    public async Task GetCaptureGapsAsync_KeepsEveryTrackedTypeAsARow()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(DateTime.UtcNow.AddDays(-1), "Person"));
            db.SaveChanges();
        }

        var matrix = await svc.GetCaptureGapsAsync(Leader(), Scope());

        // a cold row is the point of this chart, so no type may be dropped
        Assert.Contains("Personen", matrix.Rows);
        Assert.Contains("Taskforces", matrix.Rows);
        Assert.Equal(matrix.Rows.Count, matrix.Cells.Count);
        Assert.Equal(1, matrix.Max);
    }

    [Fact]
    public async Task GetCaptureGapsAsync_UntrackedEntityType_IsIgnored()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(DateTime.UtcNow.AddDays(-1), "SystemSetting"));
            db.SaveChanges();
        }

        var matrix = await svc.GetCaptureGapsAsync(Leader(), Scope());

        Assert.Equal(0, matrix.Max);
    }

    [Fact]
    public async Task GetByRecordTypeAsync_DropsSeriesWithNoActivity()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        using (var db = ctx.NewContext())
        {
            db.AuditLogs.Add(Entry(DateTime.UtcNow.AddDays(-1), "Person"));
            db.SaveChanges();
        }

        var grid = await svc.GetByRecordTypeAsync(Leader(), Scope());

        // an all-zero series would only add a dead legend entry
        Assert.Single(grid.Series);
        Assert.Equal("Personen", grid.Series[0].Name);
    }
}
