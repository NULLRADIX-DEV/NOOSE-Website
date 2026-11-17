using NOOSE_Website.Authorization;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Guards where catalog entries show up: drawer groups, app bar menus, or only search and their pages.</summary>
public class NavCatalogPlacementTests
{
    [Fact]
    public void Keys_are_unique()
    {
        var duplicates = NavCatalog.Internal.GroupBy(e => e.Key).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void Internal_entries_sit_in_the_drawer_or_an_app_bar_menu()
    {
        var places = new HashSet<NavArea>([NavArea.Primary, .. NavAreaCatalog.Areas, .. NavAreaCatalog.MenuAreas]);
        var stray = NavCatalog.Internal.Where(e => !places.Contains(e.Area)).Select(e => e.Key).ToList();
        Assert.Empty(stray);
    }

    [Fact]
    public void Every_twin_names_an_existing_entry_of_another_route()
    {
        foreach (var entry in NavCatalog.Internal.Where(e => e.DrawerTwinOf is not null))
        {
            var twin = NavCatalog.ByKey(entry.DrawerTwinOf!);
            Assert.True(twin is not null, $"{entry.Key}: Zwilling {entry.DrawerTwinOf} fehlt im Katalog");
            Assert.NotEqual(entry.Key, twin!.Key);
        }
    }

    [Fact]
    public void Every_drawer_group_and_menu_lists_something()
    {
        foreach (var area in NavAreaCatalog.Areas.Concat(NavAreaCatalog.MenuAreas))
        {
            Assert.NotEmpty(NavAreaCatalog.ListedEntries(area));
        }
    }

    [Fact]
    public void A_plain_agent_sees_a_short_drawer()
    {
        // the policy every internal agent passes; leadership-only sections stay closed
        var listed = NavAreaCatalog.Areas
            .SelectMany(NavAreaCatalog.ListedEntries)
            .Where(e => NavSectionPolicy.For(e.Section) == Policies.InternalAgent)
            .ToList();
        Assert.InRange(listed.Count, 1, 36);
    }

    [Fact]
    public void Administration_lists_one_entry_and_keeps_every_section_in_the_catalog()
    {
        var sections = NavCatalog.Internal.Where(e => e.Section == NavSection.Administration).ToList();
        Assert.Equal(AdministrationSections.All.Count, sections.Count);
        var listed = Assert.Single(sections, e => e.InDrawer);
        Assert.Equal(AdministrationSections.Root, listed.Route);
        Assert.Equal("Administration", listed.Label);
    }

    [Fact]
    public void Twins_drop_out_only_when_the_viewer_may_open_the_other_entry()
    {
        var firearm = NavCatalog.ByKey("schusswaffengebrauch")!;

        var both = NavAreaCatalog.WithoutTwins([firearm], _ => true);
        Assert.Empty(both);

        var leadershipOnly = NavAreaCatalog.WithoutTwins([firearm], e => e.Section != NavSection.Administration);
        Assert.Single(leadershipOnly);
    }
}
