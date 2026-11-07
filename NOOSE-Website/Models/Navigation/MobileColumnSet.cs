namespace NOOSE_Website.Models.Navigation;

/// <summary>Which columns of a list stay visible on phones (below 600px); the desktop always shows all of them.</summary>
public static class MobileColumnSet
{
    /// <summary>The helper class that hides an element below 600px (app.css, "Handy-Ansicht").</summary>
    public const string HiddenClass = "noose-mobil-aus";

    /// <summary>
    /// The stored choice, cleaned of keys the list no longer has; nothing stored, or nothing left after the clean-up,
    /// falls back to the list's defaults.
    /// </summary>
    /// <param name="all">Every column key of the list.</param>
    /// <param name="defaults">The columns a phone shows when the agent chose nothing.</param>
    /// <param name="stored">The agent's choice, from <see cref="NavPreferences.MobileColumns"/>; null when none.</param>
    public static IReadOnlySet<string> Resolve(IReadOnlyList<string> all, IReadOnlyList<string> defaults,
        IReadOnlyList<string>? stored)
    {
        var known = new HashSet<string>(all, StringComparer.Ordinal);
        if (stored is { Count: > 0 })
        {
            var kept = new HashSet<string>(stored.Where(known.Contains), StringComparer.Ordinal);
            if (kept.Count > 0)
            {
                return kept;
            }
        }
        return new HashSet<string>(defaults.Where(known.Contains), StringComparer.Ordinal);
    }

    /// <summary>The CSS class for one column: empty when it shows on phones, <see cref="HiddenClass"/> when not.</summary>
    public static string ClassFor(IReadOnlySet<string> visible, string key)
        => visible.Contains(key) ? string.Empty : HiddenClass;
}
