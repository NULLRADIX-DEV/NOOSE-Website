using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Migrations;
using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>The column list behind the brand sweep (Phase109): every string column is swept or has a stated reason.</summary>
/// <remarks>
/// The migration feeds the planner its own frozen model; these tests feed it the live model, so a text column or table
/// that arrives later cannot slip past unclassified, and a name in the exclusion lists cannot rot into a typo.
/// No database is needed: the model is built against the MariaDB provider without a connection.
/// </remarks>
public sealed class BrandSweepPlanTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql("server=localhost;database=none", new MariaDbServerVersion(new Version(10, 11)))
        .Options);

    public void Dispose() => _db.Dispose();

    private IReadOnlyList<BrandSweepPlan.ColumnVerdict> LivePlan() => BrandSweepPlan.Classify(_db.Model);

    /// <summary>Every (table, column) that stores text, counted without the planner.</summary>
    private HashSet<(string Table, string Column)> LiveStringColumns()
    {
        var columns = new HashSet<(string, string)>();
        foreach (var entityType in _db.Model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                var stored = property.GetValueConverter()?.ProviderClrType ?? property.ClrType;
                if (stored == typeof(string))
                {
                    columns.Add((table, property.GetColumnName(store)!));
                }
            }
        }
        return columns;
    }

    [Fact]
    public void EveryStringColumnOfTheModel_IsSweptOrExcludedWithAReason()
    {
        var expected = LiveStringColumns();
        var verdicts = LivePlan();

        Assert.NotEmpty(expected);
        Assert.Equal(expected, verdicts.Select(v => (v.Table, v.Column)).ToHashSet());
        Assert.Equal(verdicts.Count, verdicts.Select(v => (v.Table, v.Column)).Distinct().Count());
        Assert.All(verdicts.Where(v => !v.Swept), v => Assert.False(string.IsNullOrWhiteSpace(v.ExcludedBecause)));
    }

    [Fact]
    public void NoEntityTypeWithTextIsLeftWithoutATable()
    {
        // a view or keyless type would drop out of the sweep unseen
        var unmapped = _db.Model.GetEntityTypes()
            .Where(e => e.GetTableName() is null && e.GetProperties().Any(p => p.ClrType == typeof(string)))
            .Select(e => e.Name)
            .ToList();

        Assert.Empty(unmapped);
    }

    [Fact]
    public void EveryNameInTheExclusionListsExistsInTheModel()
    {
        var columns = LiveStringColumns();
        var tables = _db.Model.GetEntityTypes().Select(e => e.GetTableName()).OfType<string>().ToHashSet();

        // a name that matches nothing is a typo, or a column that was renamed away from its exclusion
        foreach (var table in BrandSweepPlan.ExcludedTables.Keys.Where(t => t != "__EFMigrationsHistory"))
        {
            Assert.Contains(table, tables);
        }
        foreach (var (prefix, suffix, _) in BrandSweepPlan.ExcludedTablePatterns.Where(p => p.Suffix.Length == 0))
        {
            Assert.Contains(tables, t => t.StartsWith(prefix, StringComparison.Ordinal));
        }
        foreach (var name in BrandSweepPlan.ExcludedColumns.Keys)
        {
            Assert.Contains(columns, c => c.Column == name);
        }
        foreach (var (suffix, _) in BrandSweepPlan.ExcludedColumnSuffixes)
        {
            Assert.Contains(columns, c => c.Column.EndsWith(suffix, StringComparison.Ordinal));
        }
        foreach (var name in BrandSweepPlan.AgentFreeTextColumns)
        {
            Assert.Contains(("AspNetUsers", name), columns);
        }
        foreach (var name in BrandSweepPlan.StrictUniqueColumns)
        {
            Assert.Contains(columns, c => c.Column == name);
        }
        foreach (var table in BrandSweepPlan.RowGuards.Keys)
        {
            Assert.Contains(table, tables);
        }
    }

    [Fact]
    public void TheAgentRowSweepsItsFourFreeTextColumnsAndNothingElse()
    {
        var swept = LivePlan().Where(v => v.Table == "AspNetUsers" && v.Swept).Select(v => v.Column).Order().ToList();

        Assert.Equal(BrandSweepPlan.AgentFreeTextColumns.Order().ToList(), swept);
    }

    [Theory]
    [InlineData("AuditLogs")]
    [InlineData("ZugriffsLogs")]
    [InlineData("KiAnfragen")]
    [InlineData("OeffentlicheFuehrungsprofile")]
    [InlineData("Suche_WortStaemme")]
    [InlineData("Suche_PhonetikSchluessel")]
    [InlineData("AspNetRoles")]
    [InlineData("AspNetUserLogins")]
    public void TheRecordsAndTheInfrastructureTablesAreNeverSwept(string table)
    {
        var verdicts = LivePlan().Where(v => v.Table == table).ToList();

        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.False(v.Swept));
    }

    [Theory]
    [InlineData("Schluessel")]
    [InlineData("SeedSchluessel")]
    [InlineData("Token")]
    [InlineData("EntitaetTyp")]
    [InlineData("VonTyp")]
    [InlineData("NachTyp")]
    [InlineData("BesitzerTyp")]
    [InlineData("Id")]
    [InlineData("ErstelltVonId")]
    public void KeysAndIdentifiersAreNeverSweptInAnyTable(string column)
    {
        var verdicts = LivePlan().Where(v => v.Column == column).ToList();

        Assert.NotEmpty(verdicts);
        Assert.All(verdicts, v => Assert.False(v.Swept));
    }

    [Fact]
    public void CaseNumbersAreSweptStrictlyAndOtherUniqueTextColumnsSkipOnAClash()
    {
        var verdicts = LivePlan();
        var statements = BrandSweepPlan.Statements(verdicts);

        var caseNumbers = verdicts.Where(v => v.Column == "Aktenzeichen").ToList();
        Assert.NotEmpty(caseNumbers);
        Assert.All(caseNumbers, v => Assert.True(v.Swept));
        Assert.DoesNotContain(statements, s => s.StartsWith("UPDATE IGNORE", StringComparison.Ordinal) && s.Contains("`Aktenzeichen` =", StringComparison.Ordinal));
        Assert.DoesNotContain(statements, s => s.StartsWith("UPDATE IGNORE", StringComparison.Ordinal) && s.Contains("`BelegNummer` =", StringComparison.Ordinal));

        var unique = verdicts.Where(v => v.Swept && v.InUniqueIndex && !BrandSweepPlan.StrictUniqueColumns.Contains(v.Column)).ToList();
        Assert.NotEmpty(unique);
        foreach (var v in unique)
        {
            Assert.Single(statements, s =>
                s.StartsWith($"UPDATE IGNORE `{v.Table}` SET ", StringComparison.Ordinal)
                && s.Contains($"`{v.Column}` = IF(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void EverySweptColumnIsRewrittenByExactlyOneStatement()
    {
        var verdicts = LivePlan();
        var statements = BrandSweepPlan.Statements(verdicts);

        foreach (var v in verdicts.Where(v => v.Swept))
        {
            Assert.Single(statements, s =>
                (s.StartsWith($"UPDATE `{v.Table}` SET ", StringComparison.Ordinal) || s.StartsWith($"UPDATE IGNORE `{v.Table}` SET ", StringComparison.Ordinal))
                && s.Contains($"`{v.Column}` = IF(INSTR(`{v.Column}`, 'base64,')", StringComparison.Ordinal));
        }
        foreach (var v in verdicts.Where(v => !v.Swept))
        {
            Assert.DoesNotContain(statements, s =>
                s.StartsWith($"UPDATE `{v.Table}` SET ", StringComparison.Ordinal) && s.Contains($"`{v.Column}` = IF(", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void TheStatementsAreStaticSqlWithoutUserVariablesOrPlaceholders()
    {
        var statements = BrandSweepPlan.Statements(LivePlan());

        Assert.NotEmpty(statements);
        // MySqlConnector reads an @name or ?name in command text as a parameter and fails on it
        Assert.All(statements, s =>
        {
            Assert.DoesNotContain('@', s);
            Assert.DoesNotContain('?', s);
            Assert.DoesNotContain("PREPARE", s, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("CALL ", s, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("UPDATE ", s, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void TheDiscordWebhookRowsOfTheSettingsAreKeptOut()
    {
        var statements = BrandSweepPlan.Statements(LivePlan());

        var settings = Assert.Single(statements, s => s.StartsWith("UPDATE `SystemEinstellungen` SET ", StringComparison.Ordinal));
        Assert.EndsWith("AND `Schluessel` NOT LIKE 'DiscordWebhook.%'", settings, StringComparison.Ordinal);
        Assert.Contains("`Wert` = IF(", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReplacementContainsATrigger_SoTheRowFilterMissesNoRewrite()
    {
        // the filter only looks for the triggers; a replacement that needs more than a trigger is a refinement of it
        Assert.NotEmpty(BrandSweepPlan.Triggers);
        Assert.All(BrandSweep.Replacements, r =>
            Assert.Contains(BrandSweepPlan.Triggers, t => r.From.Contains(t, StringComparison.Ordinal)));
        Assert.Equal("NOOSE".Length, BrandSweepPlan.ShortestTrigger);
    }

    [Theory]
    [InlineData("Die NOOSE ermittelt bei der NOOSE-Zentrale gegen die NOOSEI.")]
    [InlineData("Er arbeitet bei der NOOSE, ein Brief von der NOOSE, Zugang zur NOOSE.")]
    [InlineData("Zusammenarbeit mit der NOOSE; Leiterin der NOOSE; LSPD oder NOOSE")]
    [InlineData("https://noose.info/gesucht/NOOSE-FA-2026-0001")]
    [InlineData("Aktenzeichen NOOSE-P-2026-0001")]
    [InlineData("N.O.O.S.E. - National Office of Security Enforcement")]
    [InlineData("in der Government Facility des National Office of Security Enforcement 5020, Los Santos, San Andreas")]
    [InlineData("Noose und noose und <p class=\"noose-kasten-info\">")]
    [InlineData("Das LSPD ermittelt außer der NOOSE gegenüber der NOOSE.")]
    public void TheGeneratedReplaceChainRewritesLikeTheCodeTable(string input)
    {
        // SQLite's replace() is the same plain, case-sensitive substring swap as MariaDB's, so the chain can be run for real
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT " + BrandSweepPlan.ReplaceChain("$text");
        command.Parameters.AddWithValue("$text", input);

        Assert.Equal(BrandSweep.Apply(input), (string)command.ExecuteScalar()!);
    }

    [Fact]
    public void Phase109PlansFromItsOwnFrozenModel()
    {
        var verdicts = BrandSweepPlan.Classify(new Phase109_FibTexte().TargetModel);

        Assert.Contains(verdicts, v => v is { Table: "Personen", Column: "Aktenzeichen", Swept: true });
        Assert.Contains(verdicts, v => v is { Table: "AuditLogs", Swept: false });
        Assert.Equal(BrandSweepPlan.AgentFreeTextColumns.Order(), verdicts.Where(v => v.Table == "AspNetUsers" && v.Swept).Select(v => v.Column).Order());
        Assert.Equal(verdicts.Count, verdicts.Select(v => (v.Table, v.Column)).Distinct().Count());
    }

    [Fact]
    public void Phase109RunsEveryCommandOutsideTheTransaction()
    {
        var operations = new Phase109_FibTexte().UpOperations;

        Assert.NotEmpty(operations);
        Assert.All(operations, o => Assert.True(Assert.IsType<SqlOperation>(o).SuppressTransaction));
        // the backups come first, before the sweep touches the wording they preserve
        var sql = operations.Cast<SqlOperation>().Select(o => o.Sql).ToList();
        Assert.Equal(Phase109_FibTexte.BackupStatements, sql.Take(Phase109_FibTexte.BackupStatements.Count).ToList());
        Assert.All(Phase109_FibTexte.BackupStatements, s => Assert.StartsWith("CREATE TABLE IF NOT EXISTS ", s, StringComparison.Ordinal));
        Assert.All(sql.Skip(Phase109_FibTexte.BackupStatements.Count), s => Assert.StartsWith("UPDATE ", s, StringComparison.Ordinal));
    }

    [Fact]
    public void Phase110IsOneTransactionOfPlainDataStatements()
    {
        var operations = new Phase110_FibRaenge().UpOperations;

        Assert.NotEmpty(operations);
        Assert.All(operations, o =>
        {
            var sql = Assert.IsType<SqlOperation>(o);
            Assert.False(sql.SuppressTransaction);
            // DDL would commit the transaction implicitly and break the all-or-nothing promise
            Assert.Matches(@"^(UPDATE|DELETE) ", sql.Sql);
            Assert.DoesNotContain("CREATE ", sql.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ALTER ", sql.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("DROP ", sql.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain('@', sql.Sql);
            Assert.DoesNotContain("PREPARE", sql.Sql, StringComparison.OrdinalIgnoreCase);
        });
    }
}
