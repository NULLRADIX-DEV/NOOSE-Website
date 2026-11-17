using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Guards the V1.5 redirect map against dangling targets, cycles and slug drift.</summary>
public class LegacyRoutesTests
{
    private static (string Path, string? Tab) SplitTarget(string target)
    {
        var parts = target.Split('?', 2);
        if (parts.Length == 1)
        {
            return (parts[0], null);
        }
        var tab = parts[1].Split('&')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2 && p[0] == "tab")
            .Select(p => p[1])
            .FirstOrDefault();
        return (parts[0], tab);
    }

    [Fact]
    public void Keys_are_normalised_relative_paths()
    {
        foreach (var key in LegacyRoutes.All.Keys)
        {
            Assert.False(key.StartsWith('/'), $"'{key}' must not start with a slash");
            Assert.False(key.EndsWith('/'), $"'{key}' must not end with a slash");
            Assert.DoesNotContain('?', key);
            Assert.Equal(key.ToLowerInvariant(), key);
        }
    }

    [Fact]
    public void No_target_is_itself_a_key()
    {
        foreach (var (key, target) in LegacyRoutes.All)
        {
            var path = SplitTarget(target).Path.Trim('/');
            Assert.False(LegacyRoutes.All.ContainsKey(path),
                $"'{key}' redirects to '{path}', which is itself redirected — that is a cycle");
        }
    }

    [Fact]
    public void Every_target_names_a_known_section_of_its_page()
    {
        foreach (var (key, target) in LegacyRoutes.All)
        {
            var (path, tab) = SplitTarget(target);
            Assert.True(MergedPageSections.ByRoute.TryGetValue(path, out var slugs),
                $"'{key}' redirects to unknown page '{path}'");
            Assert.NotNull(tab);
            Assert.Contains(tab, slugs!);
        }
    }

    [Theory]
    [InlineData("admin/vorlagen?tab=dokument-vorlagen", "/einstellungen?tab=vorlagen-dokument")]
    [InlineData("admin/vorlagen?tab=personal-vorlagen", "/einstellungen?tab=vorlagen-personal")]
    [InlineData("admin/vorlagen?tab=aktivitaet-vorlagen", "/einstellungen?tab=vorlagen-aktivitaet")]
    [InlineData("admin/vorlagen", "/einstellungen?tab=vorlagen-dok")]
    [InlineData("admin/vorlagen?tab=unbekannt", "/einstellungen?tab=vorlagen-dok")]
    public void Template_subtab_survives_the_redirect(string path, string expected)
        => Assert.Equal(expected, LegacyRoutes.Target(path));

    [Theory]
    [InlineData("/admin/system", "/einstellungen?tab=system")]
    [InlineData("admin/system", "/einstellungen?tab=system")]
    [InlineData("ADMIN/SYSTEM", "/einstellungen?tab=system")]
    [InlineData("personen/papierkorb", "/papierkorb?tab=personen")]
    [InlineData("abmeldungen/papierkorb", "/abmeldungen?tab=papierkorb")]
    [InlineData("bewerbungen/sperren", "/bewerbungen?tab=sperren")]
    [InlineData("chronik", "/nachweis?tab=chronik")]
    [InlineData("admin/protokoll", "/nachweis?tab=aenderungen")]
    public void Target_resolves_removed_routes(string path, string expected)
        => Assert.Equal(expected, LegacyRoutes.Target(path));

    [Theory]
    [InlineData("chronik?zeit=7", "/nachweis?tab=chronik&zeit=7")]
    [InlineData("chronik?zeit=7&kat=Doc", "/nachweis?tab=chronik&zeit=7&kat=Doc")]
    // an old ?tab= must not win over the section the target already names
    [InlineData("chronik?tab=irgendwas&zeit=7", "/nachweis?tab=chronik&zeit=7")]
    public void Chronicle_filters_survive_the_redirect(string path, string expected)
        => Assert.Equal(expected, LegacyRoutes.Target(path));

    [Theory]
    [InlineData("organigramm", "/personal?tab=organigramm")]
    [InlineData("bestenliste", "/personal?tab=bestenliste")]
    // the period of an old Discord award post
    [InlineData("bestenliste?zeitraum=Week", "/personal?tab=bestenliste&zeitraum=Week")]
    public void Org_chart_and_leaderboard_forward_into_personnel(string path, string expected)
        => Assert.Equal(expected, LegacyRoutes.Target(path));

    [Theory]
    [InlineData("protokoll", "/nachweis?tab=aenderungen")]
    [InlineData("gegenaufklaerung", "/nachweis?tab=lagebild")]
    [InlineData("gegenaufklaerung-regeln", "/nachweis?tab=regeln")]
    public void Moved_settings_tabs_forward_to_their_new_page(string slug, string expected)
        => Assert.Equal(expected, LegacyRoutes.MovedSettingsTab(slug));

    [Theory]
    [InlineData("system")]
    [InlineData("basisdaten")]
    [InlineData(null)]
    public void Surviving_settings_tabs_are_not_forwarded(string? slug)
        => Assert.Null(LegacyRoutes.MovedSettingsTab(slug));

    [Fact]
    public void Moved_settings_tabs_are_gone_from_the_settings_page()
    {
        foreach (var slug in new[] { "protokoll", "gegenaufklaerung", "gegenaufklaerung-regeln" })
        {
            Assert.DoesNotContain(slug, MergedPageSections.Settings);
        }
    }

    [Fact]
    public void Merged_page_slugs_are_unique_per_page()
    {
        foreach (var (route, slugs) in MergedPageSections.ByRoute)
        {
            Assert.Equal(slugs.Length, slugs.Distinct(StringComparer.Ordinal).Count());
            Assert.All(slugs, s => Assert.False(string.IsNullOrWhiteSpace(s), $"{route} has a blank slug"));
        }
    }

    [Theory]
    [InlineData("personen")]
    [InlineData("")]
    [InlineData("personen/neu")]
    [InlineData("bewerbungs-vorlagen/neu")]
    [InlineData("admin/vorlagen/personal-vorlage/neu")]
    public void Target_ignores_surviving_routes(string path)
        => Assert.Null(LegacyRoutes.Target(path));

    [Theory]
    [InlineData("admin.system", "einstellungen")]
    [InlineData("admin.tags", "einstellungen")]
    [InlineData("status", "einstellungen")]
    [InlineData("doks", "fahndung")]
    [InlineData("lageberichte", "statistik")]
    [InlineData("admin.protokoll", "chronik")]
    [InlineData("bewerbungen.tests", "bewerbungen")]
    public void Orphaned_favorites_alias_to_the_absorbing_entry(string oldKey, string expected)
        => Assert.Equal(expected, LegacyRoutes.AliasKey(oldKey));

    [Fact]
    public void Alias_targets_are_real_catalog_keys()
    {
        // einstellungen/papierkorb enter the catalog with the icon rail; until then they are the only exemption
        string[] pending = ["einstellungen", "papierkorb"];
        foreach (var oldKey in LegacyRoutes.All.Keys.Select(k => k.Replace('/', '.')))
        {
            if (LegacyRoutes.AliasKey(oldKey) is { } alias && !pending.Contains(alias))
            {
                Assert.NotNull(NavCatalog.ByKey(alias));
            }
        }
    }
}
