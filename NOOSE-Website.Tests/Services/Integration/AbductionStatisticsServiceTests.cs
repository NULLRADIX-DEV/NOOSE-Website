using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Abductions;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services.Statistics;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AbductionStatisticsService"/> over in-memory SQLite.</summary>
public sealed class AbductionStatisticsServiceTests
{
    private static AbductionStatisticsService Build(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static AgentAbduction Abduction(string id, string caseNo, bool leaked, AbductionOutcome outcome,
        LeakSeverity severity, string perpId)
        => new()
        {
            Id = id, CaseNumber = caseNo, VictimAgentId = "v",
            PerpetratorType = nameof(Faction), PerpetratorId = perpId,
            Timestamp = DateTime.UtcNow.AddDays(-1),
            InformationLeaked = leaked, Outcome = outcome, LeakSeverity = leaked ? severity : LeakSeverity.None,
        };

    [Fact]
    public async Task GetAsync_CountsTotalsLeakRateAndCompromised()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("fac", "Ballas"));
            db.AgentAbductions.Add(Abduction("a1", "FIB-ENT-2026-0001", true, AbductionOutcome.Killed, LeakSeverity.High, "fac"));
            db.AgentAbductions.Add(Abduction("a2", "FIB-ENT-2026-0002", false, AbductionOutcome.Escaped, LeakSeverity.None, "fac"));
            db.AbductionCompromises.Add(new AbductionCompromise
            {
                AbductionId = "a1", TargetType = nameof(Faction), TargetId = "op1", Status = CompromiseStatus.Compromised,
            });
            db.SaveChanges();
        }
        var svc = Build(ctx);

        var stats = await svc.GetAsync(new StatisticsScope(true, StatisticsRange.Months12));

        Assert.Equal(2, stats.Total);
        Assert.Equal(1, stats.WithLeak);
        Assert.Equal(1, stats.ActiveCompromised);
        Assert.False(stats.OverTime.IsEmpty);
        // top perpetrator resolves to the seeded faction and carries both incidents
        Assert.Equal(2, (int)stats.TopPerpetrators.Series[0].Values.Sum());
    }

    [Fact]
    public async Task GetAsync_EmptyDatabase_ReturnsZeroedStats()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var stats = await svc.GetAsync(new StatisticsScope(true, StatisticsRange.Days30));

        Assert.Equal(0, stats.Total);
        Assert.Equal(0, stats.WithLeak);
        Assert.Empty(stats.TopPerpetrators.Labels);
    }
}
