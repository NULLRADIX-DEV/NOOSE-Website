using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Guards the band's stack groups: every category is placed, and no two groups share a colour.</summary>
public class ActivityBandDisplayTests
{
    private static readonly TimelineCategory[] AllCategories = Enum.GetValues<TimelineCategory>();

    [Fact]
    public void Every_category_lands_in_exactly_one_group()
    {
        foreach (var category in AllCategories)
        {
            var slot = ActivityBandDisplay.Slot(category);
            Assert.InRange(slot, 0, ActivityBandDisplay.Slots - 1);

            var owners = Enumerable.Range(0, ActivityBandDisplay.Slots)
                .Count(s => ActivityBandDisplay.Categories(s).Contains(category));
            // the remainder slot enumerates nothing, so an unplaced category is owned by none
            Assert.True(owners <= 1, $"{category} is listed in {owners} groups");
            Assert.Equal(owners == 0 ? ActivityBandDisplay.OtherSlot : slot, slot);
        }
    }

    [Fact]
    public void Groups_never_list_a_category_twice()
    {
        var seen = new HashSet<TimelineCategory>();
        for (var slot = 0; slot < ActivityBandDisplay.Slots; slot++)
        {
            foreach (var category in ActivityBandDisplay.Categories(slot))
            {
                Assert.True(seen.Add(category), $"{category} appears in more than one group");
            }
        }
    }

    [Fact]
    public void Every_group_has_its_own_colour()
    {
        var colours = Enumerable.Range(0, ActivityBandDisplay.Slots)
            .Select(ActivityBandDisplay.Hex)
            .ToList();

        // TimelineCategoryDisplay.Hex collides four ways, which is why the band has its own palette
        Assert.Equal(colours.Count, colours.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(colours, c => Assert.Matches("^#[0-9a-fA-F]{6}$", c));
    }

    [Theory]
    [InlineData("#71614A")] // NooseTheme.DefaultPrimary, overridable by an admin at runtime
    [InlineData("#3FB950")] // DefaultSecondary
    [InlineData("#9A8F80")] // DefaultTertiary
    [InlineData("#ECE8E1")] // PaletteDark.TextPrimary — a near-white bar reads as the total
    public void Group_colours_avoid_the_theme_accents(string themeColour)
    {
        var colours = Enumerable.Range(0, ActivityBandDisplay.Slots).Select(ActivityBandDisplay.Hex);
        Assert.DoesNotContain(themeColour, colours, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Every_group_has_a_label()
    {
        for (var slot = 0; slot < ActivityBandDisplay.Slots; slot++)
        {
            Assert.False(string.IsNullOrWhiteSpace(ActivityBandDisplay.Label(slot)));
        }
    }
}
