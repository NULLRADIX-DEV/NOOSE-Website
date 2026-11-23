using System.Reflection;
using Microsoft.AspNetCore.Components;

namespace NOOSE_Website.Navigation;

/// <summary>Finds the page component a path opens, from the @page templates.</summary>
public static class PageRouteTable
{
    private const int Literal = 0;
    private const int Parameter = 1;
    private const int Optional = 2;

    private sealed record Entry(Type Page, string[] Segments);

    private static readonly Lazy<IReadOnlyList<Entry>> Entries = new(Build);

    /// <summary>The page a path opens, or null when none does.</summary>
    public static Type? Find(string path)
    {
        var segments = Split(path);
        Type? best = null;
        int[]? bestRank = null;
        foreach (var entry in Entries.Value)
        {
            var rank = Match(entry.Segments, segments);
            if (rank is not null && (bestRank is null || Compare(rank, bestRank) < 0))
            {
                best = entry.Page;
                bestRank = rank;
            }
        }
        return best;
    }

    private static IReadOnlyList<Entry> Build()
        => typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(IComponent).IsAssignableFrom(t) && !t.IsAbstract)
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>(inherit: false).Select(a => (Type: t, a.Template)))
            .Where(x => !x.Template.Contains("{*", StringComparison.Ordinal))
            .Select(x => new Entry(x.Type, Split(x.Template)))
            .ToList();

    private static string[] Split(string path)
        => path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // literal beats parameter beats a skipped optional one
    private static int[]? Match(string[] template, string[] path)
    {
        if (path.Length > template.Length)
        {
            return null;
        }
        var rank = new int[template.Length];
        for (var i = 0; i < template.Length; i++)
        {
            var isParameter = template[i].Contains('{', StringComparison.Ordinal);
            if (i >= path.Length)
            {
                if (!isParameter || !template[i].EndsWith("?}", StringComparison.Ordinal))
                {
                    return null;
                }
                rank[i] = Optional;
            }
            else if (isParameter)
            {
                rank[i] = Parameter;
            }
            else if (string.Equals(template[i], path[i], StringComparison.OrdinalIgnoreCase))
            {
                rank[i] = Literal;
            }
            else
            {
                return null;
            }
        }
        return rank;
    }

    private static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            if (a[i] != b[i])
            {
                return a[i].CompareTo(b[i]);
            }
        }
        return a.Length.CompareTo(b.Length);
    }
}
