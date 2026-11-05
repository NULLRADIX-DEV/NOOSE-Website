using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using NOOSE_Website.Data;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Keeps every table under MariaDB's row size limit for utf8mb4 varchar columns.</summary>
/// <remarks>
/// MariaDB refuses a row whose non-BLOB columns exceed 65,535 bytes, and utf8mb4 counts four bytes per character.
/// SQLite has no such limit, so only this test notices before a deploy dies in the middle of a migration.
/// </remarks>
public sealed partial class MySqlRowSizeTests : IDisposable
{
    // headroom for the fixed-size columns (ids, dates, ints, flags, longtext pointers)
    private const int Budget = 60_000;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseMySql("server=localhost;database=none", new MariaDbServerVersion(new Version(10, 11)))
        .Options);

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Every_table_fits_the_utf8mb4_row_size_limit()
    {
        var model = _db.GetService<IDesignTimeModel>().Model;
        var tooLarge = model.GetEntityTypes()
            .Where(e => e.GetTableName() is not null && e.GetViewName() is null)
            .GroupBy(e => e.GetTableName()!)
            .Select(g => (Table: g.Key, Bytes: g
                .SelectMany(e => e.GetProperties())
                .DistinctBy(p => p.GetColumnName())
                .Sum(VarcharBytes)))
            .Where(t => t.Bytes > Budget)
            .Select(t => $"{t.Table}: {t.Bytes} Bytes")
            .ToList();

        Assert.True(tooLarge.Count == 0,
            "Diese Tabellen sprengen MariaDBs Zeilengrenze (65.535 Bytes, utf8mb4 = 4 Bytes je Zeichen); "
            + "lange Textspalten auf longtext umstellen: " + string.Join(", ", tooLarge));
    }

    [Fact]
    public void The_operation_free_text_fields_are_stored_off_row()
    {
        var operation = _db.GetService<IDesignTimeModel>().Model
            .FindEntityType(typeof(NOOSE_Website.Data.Entities.Operations.Operation))!;

        Assert.Equal("longtext", operation.FindProperty("Evidence")!.GetColumnType());
        Assert.Equal("longtext", operation.FindProperty("Legitimation")!.GetColumnType());
    }

    private static int VarcharBytes(IProperty property)
    {
        var match = Varchar().Match(property.GetColumnType());
        return match.Success ? int.Parse(match.Groups[1].Value) * 4 + 2 : 0;
    }

    [GeneratedRegex(@"^varchar\((\d+)\)$")]
    private static partial Regex Varchar();
}
