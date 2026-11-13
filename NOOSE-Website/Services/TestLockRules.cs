using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Which pages a running internal test may lock, and whether a path falls under a lock.</summary>
public static class TestLockRules
{
    /// <summary>The test page itself; locking it would lock the agent out of their own attempt.</summary>
    public const string TestsKey = "tests";

    /// <summary>Drawer entries a test may lock, in catalog order.</summary>
    public static IReadOnlyList<NavEntry> Lockable { get; } =
        NavCatalog.Internal.Where(e => e.Key != TestsKey).ToList();

    private static readonly Dictionary<string, int> Order =
        Lockable.Select((e, i) => (e.Key, i)).ToDictionary(x => x.Key, x => x.i, StringComparer.Ordinal);

    /// <summary>Stored keys, deduplicated and limited to lockable entries; unknown keys are dropped.</summary>
    public static IReadOnlyList<string> Parse(string? stored)
        => (stored ?? string.Empty)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(IsLockable)
            .Distinct(StringComparer.Ordinal)
            .ToList();

    /// <summary>Column value for a set of keys; null when nothing is locked.</summary>
    public static string? Serialize(IEnumerable<string>? keys)
    {
        var clean = (keys ?? [])
            .Select(k => k.Trim())
            .Where(IsLockable)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(k => Order[k])
            .ToList();
        return clean.Count == 0 ? null : string.Join('\n', clean);
    }

    public static bool IsLockable(string key)
        => key != TestsKey && NavCatalog.ByKey(key) is not null;

    /// <summary>Routes behind a set of keys.</summary>
    public static IReadOnlyList<string> Routes(IEnumerable<string> keys)
        => keys.Select(NavCatalog.ByKey).OfType<NavEntry>().Select(e => e.Route).ToList();

    /// <summary>Labels behind a set of keys, for the notice and the builder.</summary>
    public static IReadOnlyList<string> Labels(IEnumerable<string> keys)
        => keys.Select(NavCatalog.ByKey).OfType<NavEntry>().Select(e => e.Label).ToList();

    /// <summary>True when the path is a locked route or below one. Query and fragment are ignored.</summary>
    public static bool IsBlocked(string? path, IEnumerable<string> routes)
    {
        var p = Normalize(path);
        if (p.Length == 0)
        {
            return false;
        }
        foreach (var route in routes)
        {
            var r = Normalize(route);
            if (r.Length > 0 && (p == r || p.StartsWith(r + "/", StringComparison.Ordinal)))
            {
                return true;
            }
        }
        return false;
    }

    private static string Normalize(string? path)
        => Uri.UnescapeDataString((path ?? string.Empty).Split('?')[0].Split('#')[0]).Trim().Trim('/').ToLowerInvariant();
}
