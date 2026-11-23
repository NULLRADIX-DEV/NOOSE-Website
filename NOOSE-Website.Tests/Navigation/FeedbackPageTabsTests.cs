using NOOSE_Website.Navigation;

namespace NOOSE_Website.Tests.Navigation;

/// <summary>Guards the feedback page/tab picker against dangling routes and slug drift.</summary>
public class FeedbackPageTabsTests
{
    [Fact]
    public void Every_route_resolves_a_catalog_entry()
    {
        foreach (var route in FeedbackPageTabs.ByRoute.Keys)
        {
            Assert.NotNull(NavCatalog.ByRoute(route));
        }
    }

    [Fact]
    public void Merged_page_slugs_are_covered_by_the_feedback_tabs()
    {
        // MergedPageSections may lag the rail, so only its direction is asserted
        foreach (var (route, mergedSlugs) in MergedPageSections.ByRoute)
        {
            if (!FeedbackPageTabs.ByRoute.TryGetValue(route, out var tabs))
            {
                continue;
            }
            var feedbackSlugs = tabs.Select(t => t.Slug).ToList();
            foreach (var slug in mergedSlugs)
            {
                Assert.Contains(slug, feedbackSlugs);
            }
        }
    }

    [Fact]
    public void TabsFor_route_without_rail_returns_empty()
    {
        Assert.Empty(FeedbackPageTabs.TabsFor(null));
        // /papierkorb stays out of the catalog; its kinds come from ITrashService at runtime
        Assert.Empty(FeedbackPageTabs.TabsFor("/papierkorb"));
        Assert.Empty(FeedbackPageTabs.TabsFor("/personen"));
    }

    [Fact]
    public void TabsFor_is_case_insensitive()
    {
        var lower = FeedbackPageTabs.TabsFor("/einstellungen");
        var upper = FeedbackPageTabs.TabsFor("/EINSTELLUNGEN");

        Assert.NotEmpty(lower);
        Assert.Equal(lower, upper);
    }
}
