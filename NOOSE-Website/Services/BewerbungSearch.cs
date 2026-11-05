using NOOSE_Website.Data.Entities.Recruiting;

namespace NOOSE_Website.Services;

/// <summary>Free-text filter for the application overview.</summary>
public static class BewerbungSearch
{
    /// <summary>Applications whose name, case number, employer or handler contain every term of the query.</summary>
    public static List<Bewerbung> Filter(IEnumerable<Bewerbung> source, string? query)
    {
        var terms = (query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0)
        {
            return source.ToList();
        }
        return source.Where(b => terms.All(t => Matches(b, t))).ToList();
    }

    private static bool Matches(Bewerbung b, string term)
        => Contains(b.Name, term) || Contains(b.CaseNumber, term) || Contains(b.AssignedAgentName, term)
           || Contains(b.Employer, term);

    private static bool Contains(string? value, string term)
        => value?.Contains(term, StringComparison.CurrentCultureIgnoreCase) == true;
}
