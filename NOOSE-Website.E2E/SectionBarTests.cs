using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>
/// The section bar of a record page on a 375 px phone (RecordSectionRail): tapping it opens the section list and a
/// tap on another section switches to it. Measuring the width alone missed that the bar did not react at all.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class SectionBarTests(E2EFixture fixture)
{
    private const int Width = 375;
    private const int Height = 812;

    private const string Bar = ".record-rail-leiste .record-rail-leiste-knopf";
    private const string Title = ".record-rail-leiste .record-rail-leiste-titel";
    private const string OpenItems = ".mud-popover-open .mud-menu-item";

    [E2ETheory]
    [InlineData("/personen", "/personen/")]
    [InlineData("/fraktionen", "/fraktionen/")]
    public async Task Section_bar_switches_the_section(string listRoute, string prefix)
    {
        var page = await fixture.NewPageAsync(Width, Height);
        try
        {
            await RecordLinks.OpenAsync(page, listRoute);
            var detail = await RecordLinks.FindDetailPathAsync(page, prefix);
            Assert.True(detail is not null, $"{listRoute}: kein Datensatz in den Demo-Daten");
            await RecordLinks.OpenAsync(page, detail!);

            var before = (await page.Locator(Title).InnerTextAsync()).Trim();
            await page.Locator(Bar).ClickAsync();

            var items = page.Locator(OpenItems);
            await items.First.WaitForAsync(new LocatorWaitForOptions { Timeout = 15_000 });
            Assert.True(await items.CountAsync() > 1, $"{detail}: die Abschnittsleiste öffnet keine Liste");

            // the first section that is not the current one
            var target = items.Filter(new LocatorFilterOptions { HasNotText = before }).First;
            var wanted = (await target.InnerTextAsync()).Trim();
            await target.ClickAsync();

            // the switch rewrites the address with replaceState, which is no navigation: poll the bar and the address
            // instead of waiting for a load event, and give a slow CI runner time for the circuit round trip
            await Assertions.Expect(page.Locator(Title)).Not.ToHaveTextAsync(before, new LocatorAssertionsToHaveTextOptions { Timeout = 15_000 });
            await Assertions.Expect(page).ToHaveURLAsync(new Regex("[?&]tab="), new PageAssertionsToHaveURLOptions { Timeout = 15_000 });
            // the menu item may carry a count chip after its label; the bar shows the label alone
            var now = (await page.Locator(Title).InnerTextAsync()).Trim();
            Assert.StartsWith(now, wanted, StringComparison.Ordinal);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
