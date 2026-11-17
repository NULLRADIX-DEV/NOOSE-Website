using System.Collections.Concurrent;
using System.Reflection;

namespace NOOSE_Website.Services.Administration;

/// <summary>Plain text filter for the Administration lists: matches any visible text column of a row.</summary>
public static class AdministrationListFilter
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> TextProperties = new();

    public static IEnumerable<T> Apply<T>(IEnumerable<T> rows, string? query)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return terms.Length == 0 ? rows : rows.Where(r => Matches(r, terms));
    }

    /// <summary>Every term must hit some text column; ids never count.</summary>
    public static bool Matches<T>(T row, IReadOnlyCollection<string> terms)
    {
        if (row is null)
        {
            return false;
        }
        var texts = Properties(row.GetType())
            .Select(p => p.GetValue(row) as string)
            .Where(s => !string.IsNullOrEmpty(s))
            .ToList();
        return terms.All(t => texts.Any(s => s!.Contains(t, StringComparison.OrdinalIgnoreCase)));
    }

    private static PropertyInfo[] Properties(Type type) => TextProperties.GetOrAdd(type, t => t
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0)
        .Where(p => p.PropertyType == typeof(string))
        .Where(p => !p.Name.EndsWith("Id", StringComparison.Ordinal) && !p.Name.EndsWith("Ids", StringComparison.Ordinal))
        .ToArray());
}
