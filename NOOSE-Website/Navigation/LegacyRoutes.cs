using Microsoft.AspNetCore.WebUtilities;

namespace NOOSE_Website.Navigation;

/// <summary>Maps routes removed in V1.5 to the section they were merged into.</summary>
public static class LegacyRoutes
{
    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        // 14 administration pages merged into /einstellungen
        ["admin/system"] = "/einstellungen?tab=system",
        ["admin/discord"] = "/einstellungen?tab=discord",
        ["status"] = "/einstellungen?tab=status",
        ["admin/custom-felder"] = "/einstellungen?tab=custom-felder",
        ["admin/tags"] = "/einstellungen?tab=tags",
        ["tags"] = "/einstellungen?tab=tags",
        ["admin/aktualitaet"] = "/einstellungen?tab=aktualitaet",
        ["admin/bedrohungs-score"] = "/einstellungen?tab=bedrohungs-score",
        ["admin/vorlagen"] = "/einstellungen?tab=vorlagen-dok",
        ["admin/dok-vorlagen"] = "/einstellungen?tab=vorlagen-dok",
        ["dok-vorlagen"] = "/einstellungen?tab=vorlagen-dok",
        ["admin/module"] = "/einstellungen?tab=module",
        ["admin/einladungen"] = "/einstellungen?tab=einladungen",
        ["admin/partner-sichtbarkeit"] = "/einstellungen?tab=partner",
        ["admin/partner-freigabe"] = "/einstellungen?tab=partner",
        ["admin/basisdaten"] = "/einstellungen?tab=basisdaten",

        // chronicle, audit log and counter-intelligence merged into /nachweis
        ["chronik"] = "/nachweis?tab=chronik",
        ["admin/protokoll"] = "/nachweis?tab=aenderungen",

        // 12 record trash pages merged into the global recycle bin
        ["personen/papierkorb"] = "/papierkorb?tab=personen",
        ["fraktionen/papierkorb"] = "/papierkorb?tab=fraktionen",
        ["personengruppen/papierkorb"] = "/papierkorb?tab=personengruppen",
        ["parteien/papierkorb"] = "/papierkorb?tab=parteien",
        ["vorgaenge/papierkorb"] = "/papierkorb?tab=vorgaenge",
        ["operationen/papierkorb"] = "/papierkorb?tab=operationen",
        ["taskforces/papierkorb"] = "/papierkorb?tab=taskforces",
        ["aufgaben/papierkorb"] = "/papierkorb?tab=aufgaben",
        ["brett/papierkorb"] = "/papierkorb?tab=brett",
        ["kalender/papierkorb"] = "/papierkorb?tab=kalender",
        ["besprechungen/papierkorb"] = "/papierkorb?tab=besprechungen",
        ["aktivitaeten/papierkorb"] = "/papierkorb?tab=aktivitaeten",

        // absences keep their own trash next to the overview they belong to
        ["abmeldungen/papierkorb"] = "/abmeldungen?tab=papierkorb",
        ["abmeldungen/uebersicht"] = "/abmeldungen?tab=uebersicht",

        // cross-record overviews merged into the pages that own them
        ["observationen"] = "/fahndung?tab=observationen",
        ["doks"] = "/fahndung?tab=doks",
        ["bewerbungen/sperren"] = "/bewerbungen?tab=sperren",
        ["bewerbungs-vorlagen"] = "/bewerbungen?tab=vorlagen",
        ["bewerbungs-tests"] = "/bewerbungen?tab=tests",
        ["lageberichte"] = "/statistik?tab=lageberichte",

        // org chart and leaderboard became sections of /personal
        ["organigramm"] = "/personal?tab=organigramm",
        ["bestenliste"] = "/personal?tab=bestenliste",
    };

    /// <summary>Old ?tab= values of the former /admin/vorlagen tab page.</summary>
    private static readonly Dictionary<string, string> TemplateTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dok-vorlagen"] = "vorlagen-dok",
        ["dokument-vorlagen"] = "vorlagen-dokument",
        ["aktivitaet-vorlagen"] = "vorlagen-aktivitaet",
        ["personal-vorlagen"] = "vorlagen-personal",
    };

    /// <summary>Settings sections that moved to another page; the settings page forwards these itself,
    /// because /einstellungen survives and <see cref="Target"/> only matches whole removed routes.</summary>
    private static readonly Dictionary<string, string> MovedSettingsTabs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["protokoll"] = "/nachweis?tab=aenderungen",
        ["gegenaufklaerung"] = "/nachweis?tab=lagebild",
        ["gegenaufklaerung-regeln"] = "/nachweis?tab=regeln",
    };

    /// <summary>Nav keys that no longer exist, mapped to the entry that absorbed them.</summary>
    private static readonly Dictionary<string, string> KeyAliases = new(StringComparer.Ordinal)
    {
        ["observationen"] = "fahndung",
        ["doks"] = "fahndung",
        ["abmeldungen.uebersicht"] = "abmeldungen",
        ["lageberichte"] = "statistik",
        ["bewerbungen.tests"] = "bewerbungen",
        ["bewerbungen.vorlagen"] = "bewerbungen",
        ["admin.einladungen"] = "einstellungen",
        ["admin.tags"] = "einstellungen",
        ["admin.vorlagen"] = "einstellungen",
        ["admin.custom-felder"] = "einstellungen",
        ["admin.aktualitaet"] = "einstellungen",
        ["admin.bedrohungs-score"] = "einstellungen",
        ["admin.basisdaten"] = "einstellungen",
        // the audit log left /einstellungen for /nachweis, whose catalog key is still "chronik"
        ["admin.protokoll"] = "chronik",
        ["admin.discord"] = "einstellungen",
        ["admin.module"] = "einstellungen",
        ["admin.partner-sichtbarkeit"] = "einstellungen",
        ["admin.partner-freigabe"] = "einstellungen",
        ["admin.system"] = "einstellungen",
        ["status"] = "einstellungen",
    };

    /// <summary>All removed routes and their targets; drives the redirect shell and its test.</summary>
    public static IReadOnlyDictionary<string, string> All => Map;

    /// <summary>Surviving nav key for a stored favorite that pointed at a merged entry, or null.</summary>
    public static string? AliasKey(string key) => KeyAliases.GetValueOrDefault(key);

    /// <summary>New home of a settings section that moved to another page, or null when it still lives there.</summary>
    public static string? MovedSettingsTab(string? slug)
        => string.IsNullOrWhiteSpace(slug) ? null : MovedSettingsTabs.GetValueOrDefault(slug);

    /// <summary>Merged destination for a removed route, or null when the route is unknown.</summary>
    public static string? Target(string? relativePath)
    {
        var raw = (relativePath ?? string.Empty).Split('#')[0];
        var split = raw.Split('?', 2);
        var path = split[0].Trim('/');
        if (path.Length == 0 || !Map.TryGetValue(path, out var target))
        {
            return null;
        }

        // the old template page carried its sub-tab in the query; keep the user on it
        if (split.Length == 2 && path.EndsWith("vorlagen", StringComparison.OrdinalIgnoreCase))
        {
            var query = QueryHelpers.ParseQuery(split[1]);
            if (query.TryGetValue("tab", out var tab) && tab.Count > 0
                && tab[0] is { } slug && TemplateTabs.TryGetValue(slug, out var mapped))
            {
                return $"/einstellungen?tab={mapped}";
            }
        }

        // the chronicle keeps its filters, the leaderboard its ?zeitraum= (old Discord award posts link it)
        if (split.Length == 2 && (string.Equals(path, "chronik", StringComparison.OrdinalIgnoreCase)
                                  || string.Equals(path, "bestenliste", StringComparison.OrdinalIgnoreCase)))
        {
            return WithCarriedQuery(target, split[1]);
        }
        return target;
    }

    // values arrive decoded from ParseQuery, so AddQueryString must escape them exactly once
    private static string WithCarriedQuery(string target, string query)
    {
        var carried = new List<KeyValuePair<string, string?>>();
        foreach (var (key, values) in QueryHelpers.ParseQuery(query))
        {
            // the target already names its section; an old ?tab= must not win over it
            if (string.Equals(key, "tab", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            foreach (var value in values)
            {
                carried.Add(new KeyValuePair<string, string?>(key, value));
            }
        }
        return carried.Count == 0 ? target : QueryHelpers.AddQueryString(target, carried);
    }
}
