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
            // the page policy lets a partner in
            ["/fahndung"] = Sections(Group(Policies.InternalAgent, "oeffentlich", "organisationen", "einsprueche")),
            ["/personal"] = Sections(Group(Policies.InternalAgent, "bestenliste")),
            ["/abmeldungen"] = Sections(Group(Policies.LeadershipPage, "uebersicht", "papierkorb")),
            ["/statistik"] = Sections(
                Group(Policies.LeadershipPage, "kasse", "finanzierung", "verpflegung", "dienststelle", "lageberichte"),
                Group(Policies.Admin, "nutzung")),
        };

    /// <summary>Policy that guards the section, or null when it is unguarded.</summary>
    public static string? For(string route, string slug)
        => ByRoute.TryGetValue(route, out var sections) && sections.TryGetValue(slug, out var policy) ? policy : null;

    private static IReadOnlyDictionary<string, string> Sections(params (string Policy, string[] Slugs)[] groups)
        => groups.SelectMany(g => g.Slugs.Select(s => (Slug: s, g.Policy)))
            .ToDictionary(x => x.Slug, x => x.Policy, StringComparer.OrdinalIgnoreCase);

    private static (string Policy, string[] Slugs) Group(string policy, params string[] slugs) => (policy, slugs);
}
