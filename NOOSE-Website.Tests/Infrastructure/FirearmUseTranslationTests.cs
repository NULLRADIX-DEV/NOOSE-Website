using NOOSE_Website.Data;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Proves the firearm-use queries translate against the production provider, not just against SQLite.</summary>
/// <remarks>
/// <c>ToQueryString()</c> compiles the query without touching a database, so this needs no MySQL server - only the
/// provider. The shapes are the ones <c>FirearmUseService</c> runs: the id filter of the division, the projection over
/// the agent navigation and the recipient query of the bell.
/// </remarks>
public sealed class FirearmUseTranslationTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql("server=localhost;database=none", ServerVersion.Parse("8.0.0-mysql"))
        .Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TheOverviewQuery_TranslatesWithTheDivisionIdsAndThePeriod()
    {
        List<string> members = ["a1", "a2"];
        var from = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = from.AddDays(1);

        var sql = _db.FirearmUses.AsNoTracking()
            .Where(x => x.AgentId == "a1" && x.Timestamp >= from && x.Timestamp < to && members.Contains(x.AgentId))
            .OrderByDescending(x => x.Timestamp)
            .Take(FirearmUseService.MaxRows)
            .Select(x => new
            {
                x.Id,
                x.AgentId,
                Codename = x.Agent!.Codename,
                Rank = x.Agent!.Rank,
                Division = x.Agent!.Division,
                IsTru = x.Agent!.IsTRU,
                Partner = x.Agent!.PartnerAgency,
                x.Timestamp,
                x.Reason,
            })
            .ToQueryString();

        Assert.Contains("ORDER BY", sql, StringComparison.Ordinal);
        Assert.Contains("LIMIT", sql, StringComparison.Ordinal);
        Assert.Contains("IN (", sql, StringComparison.Ordinal);
        Assert.Contains("Schusswaffengebraeuche", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDivisionMembers_TranslateAndTheHomeIsResolvedInMemory()
    {
        var sql = _db.Users.AsNoTracking().Where(a => a.Rank != null && a.PartnerAgency == null)
            .Select(a => new { a.Id, a.Rank, a.Division, a.IsTRU })
            .ToQueryString();

        Assert.Contains("SELECT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBellRecipients_TranslateWithTheRankFloor()
    {
        var sql = _db.Users.AsNoTracking().OnlySelectable()
            .Where(u => u.Rank >= FirearmUseRules.NotifyFloor && u.Id != "a1")
            .Select(u => new { u.Id, u.Rank, u.Division, u.IsTRU })
            .ToQueryString();

        Assert.Contains("WHERE", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheAgentOptions_TranslateAsADistinctOverTheReports()
    {
        var sql = _db.FirearmUses.AsNoTracking().Select(x => x.AgentId).Distinct().ToQueryString();

        Assert.Contains("DISTINCT", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTrashQuery_TranslatesPastTheSoftDeleteFilter()
    {
        var sql = _db.FirearmUses.IgnoreQueryFilters().AsNoTracking()
            .Include(x => x.Agent)
            .Where(x => x.IsDeleted)
            .OrderByDescending(x => x.DeletedAt)
            .ToQueryString();

        Assert.Contains("JOIN", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TheModelUsesTheGermanTableAndColumnNames()
    {
        var sql = _db.FirearmUses.IgnoreQueryFilters().ToQueryString();

        Assert.Contains("`Schusswaffengebraeuche`", sql, StringComparison.Ordinal);
        Assert.Contains("`Zeitpunkt`", sql, StringComparison.Ordinal);
        Assert.Contains("`Begruendung`", sql, StringComparison.Ordinal);
        Assert.Contains("`IstGeloescht`", sql, StringComparison.Ordinal);
    }
}
