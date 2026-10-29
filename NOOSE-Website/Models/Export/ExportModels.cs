using System.Globalization;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Export;

/// <summary>A finished export, ready to hand to the browser.</summary>
public sealed record ExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>One CSV column of a list export: the list's own heading and how a row reads in it.</summary>
public sealed record ExportColumn<T>(string Title, Func<T, string?> Value);

/// <summary>The record lists and dossiers that may leave as a file, and the route slug their files are named after.</summary>
public static class ExportableRecords
{
    private static readonly Dictionary<string, string> Slugs = new()
    {
        [nameof(Person)] = "personen",
        [nameof(Faction)] = "fraktionen",
        [nameof(PersonGroup)] = "personengruppen",
        [nameof(Party)] = "parteien",
        [nameof(Case)] = "vorgaenge",
        [nameof(Operation)] = "operationen",
    };

    public static bool Contains(string recordType) => Slugs.ContainsKey(recordType);

    /// <summary>The list's route segment; throws for a type that may not be exported.</summary>
    public static string Slug(string recordType)
        => Slugs.TryGetValue(recordType, out var slug)
            ? slug
            : throw new ArgumentException($"'{recordType}' wird nicht exportiert.", nameof(recordType));
}

public static class ListExport
{
    public static IEnumerable<string> Head<T>(IReadOnlyList<ExportColumn<T>> columns) => columns.Select(c => c.Title);

    /// <summary>One CSV row per item, in the order given; an empty value stays an empty field.</summary>
    public static IEnumerable<IEnumerable<string>> Rows<T>(IEnumerable<T> items, IReadOnlyList<ExportColumn<T>> columns)
        => items.Select(item => columns.Select(c => c.Value(item) ?? string.Empty));
}

/// <summary>The few cell shapes several lists share, written as the screen shows them.</summary>
public static class ExportText
{
    /// <summary>The hazard chip with the score its tooltip names, e.g. "Kritisch (82)"; empty without a score.</summary>
    public static string Hazard(HazardLevel level, int? score)
        => score is { } s ? $"{HazardLevelLogic.Name(level)} ({s})" : "";

    /// <summary>The lock chip's label; empty for an open record.</summary>
    public static string Secrecy(bool classified, DocumentClassification level)
        => classified ? DocumentClassificationDisplay.ChipLabel(level) : "";

    public static string Flag(bool value) => value ? "Ja" : "";

    public static string Stamp(DateTime local) => local.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture);

    public static string Day(DateTime? local) => local?.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) ?? "";
}

/// <summary>How an export reads in the access log. No column says "export", so the entity type does.</summary>
public static class ExportLog
{
    public const string Type = "Export";

    public static string List(string recordType, int count) => $"{RecordTypeDisplay.Plural(recordType)}-Liste ({count})";

    public static string Dossier(string recordType, string recordId) => $"{recordType}:{recordId}";
}
