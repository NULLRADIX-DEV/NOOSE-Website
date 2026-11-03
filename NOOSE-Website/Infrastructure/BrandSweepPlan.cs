using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Infrastructure;

/// <summary>Decides which text columns the NOOSE to FIB data sweep rewrites and builds its UPDATE statements.</summary>
/// <remarks>
/// <para>
/// One static <c>UPDATE</c> per table over all of its swept columns, so a row is rewritten once and every statement
/// is its own command with its own timeout. No <c>PREPARE</c>, no user variables and no procedure: MySqlConnector
/// rejects an <c>@name</c> in command text, and a generated statement is something a reviewer can read.
/// </para>
/// <para>
/// The planner reads a model, so the migration feeds it its own frozen <c>TargetModel</c> (a later column never
/// changes what Phase109 does) and the test feeds it the live <c>AppDbContext.Model</c> (a column that arrives later
/// must still be swept or carry a reason). Everything is swept except what is listed here with a reason: the
/// records, the identifiers and keys a rewrite would break, and files named on disk.
/// </para>
/// </remarks>
public static class BrandSweepPlan
{
    /// <summary>The verdict for one string column of the model.</summary>
    /// <param name="Table">Table name.</param>
    /// <param name="Column">Column name.</param>
    /// <param name="ExcludedBecause">Why the column is left alone; null when it is swept.</param>
    /// <param name="InUniqueIndex">Whether the column belongs to a unique index (or alternate key).</param>
    public sealed record ColumnVerdict(string Table, string Column, string? ExcludedBecause, bool InUniqueIndex)
    {
        public bool Swept => ExcludedBecause is null;
    }

    /// <summary>Case numbers stay strict: only the shared prefix changes, so the swap keeps them unique and a collision
    /// is a real error. Every other unique text column runs as <c>UPDATE IGNORE</c> and a clash skips the row.</summary>
    public static IReadOnlyCollection<string> StrictUniqueColumns { get; } = ["Aktenzeichen", "BelegNummer"];

    /// <summary>The four free-text columns of the agent row; the rest of <c>AspNetUsers</c> is account data.</summary>
    public static IReadOnlyCollection<string> AgentFreeTextColumns { get; } =
        ["GesperrtGrund", "Kuendigungsgrund", "GekuendigtVonName", "InaktivNachgefasstVonName"];

    /// <summary>Tables that are never swept, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> ExcludedTables { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["AuditLogs"] = "the audit trail records what was written at the time",
        ["ZugriffsLogs"] = "the access log records what was read at the time",
        ["KiAnfragen"] = "the AI request log is a record, and its fingerprint covers the original prompt",
        ["__EFMigrationsHistory"] = "migration bookkeeping",
        ["OeffentlicheFuehrungsprofile"] = "retired table, kept only so a rollback can read it",
    };

    /// <summary>Table name prefixes and suffixes that are never swept, with the reason.</summary>
    public static IReadOnlyList<(string Prefix, string Suffix, string Reason)> ExcludedTablePatterns { get; } =
    [
        ("Suche_", "", "derived search index, rebuilt by the backfill worker"),
        ("", "_Sicherung", "backup of the original wording"),
        ("AspNet", "", "identity data; only the four free-text columns of the agent row are swept"),
    ];

    /// <summary>Column names that are never swept, with the reason.</summary>
    public static IReadOnlyDictionary<string, string> ExcludedColumns { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Token"] = "secret token",
        ["ProviderKey"] = "login provider key",
        ["LoginProvider"] = "login provider name",
        // the suffix rule below skips a bare name, and a column called just Schluessel is a lookup key (value list, signal, module)
        ["Schluessel"] = "key that code or seeders look rows up by",
        ["Slug"] = "address of a page, referenced by links",
        ["ContentType"] = "media type",
        ["DateinameGespeichert"] = "file name on disk, assigned by the server",
        ["DateiName"] = "file name on disk, assigned by the server",
        ["AnhangDateiname"] = "file name on disk, assigned by the server",
        ["FotoDateiname"] = "file name on disk, assigned by the server",
        ["BildDatei"] = "file name on disk, assigned by the server",
        ["Profilbild"] = "file name on disk, assigned by the server",
        ["AusstehendesProfilbild"] = "file name on disk, assigned by the server",
    };

    /// <summary>Column name endings that are never swept, with the reason.</summary>
    public static IReadOnlyList<(string Suffix, string Reason)> ExcludedColumnSuffixes { get; } =
    [
        ("Id", "identifier or reference to a row"),
        ("Typ", "type discriminator that code compares against"),
        ("Schluessel", "key that code or seeders look rows up by"),
        ("Hash", "hash"),
        ("Stamp", "concurrency or security stamp"),
        ("Stempel", "fingerprint of a scope"),
        ("Fingerabdruck", "fingerprint"),
    ];

    /// <summary>Rows that stay out of a swept table, as a SQL condition.</summary>
    public static IReadOnlyDictionary<string, string> RowGuards { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // the webhook URL carries a token; a chance match of a word in it must never rewrite it
        ["SystemEinstellungen"] =
            $"`Schluessel` NOT LIKE '{SystemSettingKeys.DiscordWebhookPrefix}%'",
    };

    /// <summary>The replacements that can start a rewrite: every <c>From</c> that does not already contain a shorter
    /// one. A row without any of them cannot change, because the first swap that fires needs its <c>From</c> in the
    /// original text; the longer swaps are only a refinement of these.</summary>
    public static IReadOnlyList<string> Triggers { get; } = BuildTriggers();

    /// <summary>A column shorter than the shortest trigger cannot hold a match.</summary>
    public static int ShortestTrigger { get; } = Triggers.Min(t => t.Length);

    /// <summary>Classifies every string column of the model.</summary>
    public static IReadOnlyList<ColumnVerdict> Classify(IReadOnlyModel model)
    {
        var verdicts = new List<ColumnVerdict>();
        var seen = new HashSet<(string, string)>();

        foreach (var entityType in model.GetEntityTypes())
        {
            var table = entityType.GetTableName();
            if (table is null)
            {
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());
            foreach (var property in entityType.GetProperties())
            {
                if (!IsStringColumn(property))
                {
                    continue;
                }

                var column = property.GetColumnName(store) ?? property.GetColumnName();
                if (!seen.Add((table, column)))
                {
                    continue; // table shared by several entity types
                }

                verdicts.Add(new ColumnVerdict(table, column, ExclusionOf(table, column, property), InUniqueIndex(entityType, property)));
            }
        }

        return verdicts;
    }

    /// <summary>One or two statements per table: strict columns first, then the unique text columns as <c>UPDATE IGNORE</c>.</summary>
    public static IReadOnlyList<string> Statements(IEnumerable<ColumnVerdict> verdicts)
    {
        var statements = new List<string>();
        foreach (var table in verdicts.Where(v => v.Swept).GroupBy(v => v.Table).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var strict = table.Where(v => !v.InUniqueIndex || StrictUniqueColumns.Contains(v.Column)).ToList();
            var skipOnClash = table.Where(v => v.InUniqueIndex && !StrictUniqueColumns.Contains(v.Column)).ToList();

            if (strict.Count > 0)
            {
                statements.Add(Update(table.Key, strict, ignore: false));
            }
            if (skipOnClash.Count > 0)
            {
                statements.Add(Update(table.Key, skipOnClash, ignore: true));
            }
        }
        return statements;
    }

    /// <summary>The ordered swaps of <see cref="BrandSweep.Replacements"/> as nested <c>REPLACE</c> around an operand.</summary>
    public static string ReplaceChain(string operand)
    {
        var expression = operand;
        foreach (var swap in BrandSweep.Replacements)
        {
            expression = $"REPLACE({expression}, {Literal(swap.From)}, {Literal(swap.To)})";
        }
        return expression;
    }

    private static string Update(string table, IReadOnlyList<ColumnVerdict> columns, bool ignore)
    {
        var sql = new StringBuilder(ignore ? "UPDATE IGNORE `" : "UPDATE `").Append(table).Append("` SET ");
        sql.AppendJoin(", ", columns.Select(c =>
            $"`{c.Column}` = IF(INSTR(`{c.Column}`, 'base64,') > 0, `{c.Column}`, {ReplaceChain($"`{c.Column}`")})"));

        // BINARY: the columns are utf8mb4_general_ci, and the swaps are case-sensitive
        sql.Append(" WHERE (");
        sql.AppendJoin(" OR ", columns.SelectMany(c =>
            Triggers.Select(t => $"INSTR(BINARY `{c.Column}`, {Literal(t)}) > 0")));
        sql.Append(')');

        if (RowGuards.TryGetValue(table, out var guard))
        {
            sql.Append(" AND ").Append(guard);
        }
        return sql.ToString();
    }

    private static string? ExclusionOf(string table, string column, IReadOnlyProperty property)
    {
        if (ExcludedTables.TryGetValue(table, out var tableReason))
        {
            return tableReason;
        }

        foreach (var (prefix, suffix, reason) in ExcludedTablePatterns)
        {
            var matches = table.StartsWith(prefix, StringComparison.Ordinal) && table.EndsWith(suffix, StringComparison.Ordinal);
            if (!matches)
            {
                continue;
            }

            var agentFreeText = table == "AspNetUsers" && AgentFreeTextColumns.Contains(column);
            if (!agentFreeText)
            {
                return reason;
            }
        }

        if (property.IsKey() || property.IsForeignKey())
        {
            return "key or foreign key";
        }
        if (ExcludedColumns.TryGetValue(column, out var columnReason))
        {
            return columnReason;
        }
        foreach (var (suffix, reason) in ExcludedColumnSuffixes)
        {
            // an ending, not the whole name: a column called just "Typ" is free text (case and operation type)
            if (column.Length > suffix.Length && column.EndsWith(suffix, StringComparison.Ordinal))
            {
                return reason;
            }
        }
        if (property.GetMaxLength() is { } length && length < ShortestTrigger)
        {
            return $"shorter than the shortest swap ({ShortestTrigger} characters)";
        }
        return null;
    }

    /// <summary>String as stored: a text property, or an enum or value object the model converts to text.</summary>
    private static bool IsStringColumn(IReadOnlyProperty property)
        => (property.GetValueConverter()?.ProviderClrType ?? property.ClrType) == typeof(string);

    private static bool InUniqueIndex(IReadOnlyEntityType entityType, IReadOnlyProperty property)
        => entityType.GetIndexes().Any(i => i.IsUnique && i.Properties.Contains(property))
           || entityType.GetKeys().Any(k => k.Properties.Contains(property));

    private static List<string> BuildTriggers()
    {
        var all = BrandSweep.Replacements.Select(r => r.From).Distinct(StringComparer.Ordinal).ToList();
        return all.Where(candidate => !all.Any(other => other.Length < candidate.Length && candidate.Contains(other, StringComparison.Ordinal))).ToList();
    }

    /// <summary>A SQL string literal; the swaps hold neither quotes nor backslashes today, but a later one must not break the statement.</summary>
    private static string Literal(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal) + "'";
}
