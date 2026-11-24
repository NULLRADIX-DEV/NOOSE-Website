using NOOSE_Website.Authorization;

namespace NOOSE_Website.Navigation;

/// <summary>Policy of each rail section a page guards with an AuthorizeView.</summary>
public static class SectionPolicies
{
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> ByRoute =
        new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["/einstellungen"] = Sections(
                Group(Policies.AdminPage, "system", "status", "partner", "oeffentliche-module"),
                Group(Policies.CounterIntel, "ki-anfragen", "oeffentliche-fuehrung")),
            ["/nachweis"] = Sections(
                Group(Policies.LeadershipPage, "aenderungen", "zugriffe"),
                Group(Policies.CounterIntel, "lagebild", "agenten-profil", "auffaelligkeiten", "regeln")),
            ["/finanzierungen"] = Sections(Group(Policies.LeadershipPage, "alle", "budgets", "statistik", "papierkorb")),
            ["/verpflegung"] = Sections(Group(Policies.LeadershipPage, "alle", "limits", "statistik", "papierkorb")),
            ["/kasse"] = Sections(Group(Policies.LeadershipPage, "vorlagen", "statistik", "papierkorb")),
            ["/fahndung"] = Sections(Group(Policies.InternalAgent, "oeffentlich", "organisationen", "einsprueche")),
            ["/personal"] = Sections(Group(Policies.InternalAgent, "bestenliste")),
            ["/abmeldungen"] = Sections(Group(Policies.LeadershipPage, "uebersicht", "papierkorb")),
            ["/tests"] = Sections(Group(Policies.HrbOrLeadership, "korrektur", "katalog")),
            ["/tickets"] = Sections(
                Group(Policies.TicketDesk, "offen", "bearbeitung", "wartet", "geschlossen"),
                Group(Policies.InternalAgent, "beteiligt")),
            ["/dienstverordnung"] = Sections(Group(Policies.LeadershipPage, "papierkorb")),
            ["/entfuehrungen"] = Sections(Group(Policies.LeadershipPage, "papierkorb")),
            ["/asservatenkammer"] = Sections(Group(Policies.LeadershipPage, "papierkorb")),
            ["/anordnungen"] = Sections(Group(Policies.LeadershipPage, "papierkorb")),
            ["/statistik"] = Sections(
                Group(Policies.LeadershipPage, "kasse", "finanzierung", "verpflegung", "dienststelle", "lageberichte"),
                Group(Policies.Admin, "nutzung")),
            // a page with several templates has one entry, keyed by any of them
            ["/dienstverordnung/absatz/{Id}"] = Sections(Group(Policies.LeadershipPage, "kenntnisnahme")),
        };

    private static readonly Lazy<IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>>> ByPage = new(() => PagesOf(ByRoute));

    /// <summary>Policy that guards the section, or null when it is unguarded.</summary>
    public static string? For(string route, string slug) => For(route, slug, ByPage.Value);

    /// <summary>Same lookup against another table, keyed like <see cref="ByRoute"/>.</summary>
    public static string? For(string route, string slug, IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byRoute)
        => For(route, slug, PagesOf(byRoute));

    private static string? For(string route, string slug, IReadOnlyDictionary<Type, IReadOnlyDictionary<string, string>> byPage)
    {
        var page = PageRouteTable.Find(route);
        return page is not null && byPage.TryGetValue(page, out var sections) && sections.TryGetValue(slug, out var policy)
            ? policy
            : null;
    }

    // one entry per page, whichever of its templates the key names
    private static Dictionary<Type, IReadOnlyDictionary<string, string>> PagesOf(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> byRoute)
    {
        var pages = new Dictionary<Type, IReadOnlyDictionary<string, string>>();
        foreach (var (route, sections) in byRoute)
        {
            var page = PageRouteTable.Find(route)
                ?? throw new InvalidOperationException($"SectionPolicies: no page opens {route}");
            if (!pages.TryAdd(page, sections))
            {
                throw new InvalidOperationException($"SectionPolicies: {page.Name} has two entries ({route})");
            }
        }
        return pages;
    }

    private static IReadOnlyDictionary<string, string> Sections(params (string Policy, string[] Slugs)[] groups)
        => groups.SelectMany(g => g.Slugs.Select(s => (Slug: s, g.Policy)))
            .ToDictionary(x => x.Slug, x => x.Policy, StringComparer.OrdinalIgnoreCase);

    private static (string Policy, string[] Slugs) Group(string policy, params string[] slugs) => (policy, slugs);
}
