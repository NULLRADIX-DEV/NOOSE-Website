using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Holds the migrations against the model and the code defaults they must agree with.</summary>
/// <remarks>
/// Every other test runs on SQLite with <c>EnsureCreated</c>, so nothing else notices a model change that has no
/// migration - and at startup EF Core 9 throws on exactly that, which keeps the site down. Like
/// <see cref="MySqlTranslationTests"/> this builds the production provider without a connection.
/// </remarks>
public sealed class MigrationModelTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql("server=localhost;database=none", new MariaDbServerVersion(new Version(10, 11)))
        .Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TheModelHasNoChangesWithoutAMigration()
        => Assert.False(
            _db.Database.HasPendingModelChanges(),
            "Das Modell weicht vom Snapshot ab: 'dotnet ef migrations add PhaseNN_<Name>' aus scripts/ erzeugen "
            + "(oder die Entitaetsaenderung zuruecknehmen), sonst startet die App in Produktion nicht.");

    [Fact]
    public void TheRankRemapCoversEveryColumnThatStoresARank()
    {
        // created after the cutover, born with the new rank numbers
        HashSet<(string Table, string Column)> bornNew = [("Anordnungen", "EntscheiderDienstgrad")];
        var stored = new HashSet<(string Table, string Column)>();
        foreach (var entityType in _db.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var store = Microsoft.EntityFrameworkCore.Metadata.StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties().Where(p => (Nullable.GetUnderlyingType(p.ClrType) ?? p.ClrType) == typeof(Rank)))
            {
                stored.Add((table, property.GetColumnName(store)!));
            }
        }

        var listed = FibCutover.RankColumns.SelectMany(t => t.Columns.Select(c => (t.Table, Column: c))).ToHashSet();

        Assert.Equal(11, listed.Count);
        stored.ExceptWith(bornNew);
        Assert.Equal(listed, stored);
    }

    [Fact]
    public void TheFinancingStartersAreTheNumbersOfTheCodeDefault()
    {
        var defaults = FinancingBudgetConfig.Default();

        Assert.Equal([5, 6], FibCutover.FinancingStarters.Select(s => s.Rank).ToArray());
        foreach (var (rank, baseMonthly, carryOver) in FibCutover.FinancingStarters)
        {
            var budget = defaults.For((Rank)rank);
            Assert.Equal(baseMonthly, budget.BaseMonthly);
            Assert.Equal(carryOver, budget.CarryOverPercent);
        }
    }

    [Fact]
    public void TheQuotaStartersAreTheNumbersOfTheCodeDefault()
    {
        var defaults = LlmQuotaConfig.Default();

        Assert.Equal([5, 6], FibCutover.QuotaStarters.Select(s => s.Rank).ToArray());
        foreach (var (rank, baseWeekly, carryOver, daily) in FibCutover.QuotaStarters)
        {
            var quota = defaults.For((Rank)rank);
            Assert.Equal(baseWeekly, quota.BaseWeekly);
            Assert.Equal(carryOver, quota.CarryOverPercent);
            Assert.Equal(daily, quota.DailyPercent);
        }
    }

    [Fact]
    public void TheNewRanksSitWhereTheRemapPutsThem()
    {
        // 5 and 6 become Section Chief and Assistant Director, the old Deputy Director and Director land on 7 and 8
        Assert.Equal(5, (int)Rank.SectionChief);
        Assert.Equal(6, (int)Rank.AssistantDirector);
        Assert.Equal(7, (int)Rank.DeputyDirector);
        Assert.Equal(8, (int)Rank.Director);
    }
}
