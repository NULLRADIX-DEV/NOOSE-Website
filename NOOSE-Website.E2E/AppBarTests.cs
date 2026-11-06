using System.Text.Json;
using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>App bar and drawer on a 375 px phone (as the demo Director sees them).</summary>
[Collection(E2ECollection.Name)]
public sealed class AppBarTests(E2EFixture fixture)
{
    private const int Width = 375;
    private const int Height = 812;

    // the narrow menu button; the desktop one is display:none below 960 px
    private const string MenuButton = ".mud-appbar button[aria-label='Navigation umschalten'] >> visible=true";

    // Every visible control of the app bar, measured against the screen (the visual viewport, not the layout one).
    private const string AppBarScript = """
        () => {
          const vv = window.visualViewport;
          const bar = document.querySelector('.mud-appbar');
          const controls = [...bar.querySelectorAll('button, a, input')].filter(e => {
            const r = e.getBoundingClientRect();
            return r.width > 0 && r.height > 0 && getComputedStyle(e).visibility !== 'hidden';
          });
          return {
            screenWidth: vv.width,
            outside: controls
              .filter(e => { const r = e.getBoundingClientRect(); return r.left < -0.5 || r.right > vv.width + 0.5; })
              .map(e => (e.getAttribute('aria-label') || e.textContent || e.tagName).trim()),
            labels: controls.map(e => e.getAttribute('aria-label') || '')
          };
        }
        """;

    // Where the drawer, its first rail button and its two scrolling columns sit relative to the screen.
    private const string DrawerScript = """
        () => {
          const vv = window.visualViewport;
          const drawer = document.querySelector('.noose-drawer');
          const first = drawer.querySelector('.noose-nav-rail .mud-icon-button');
          const column = sel => {
            const e = drawer.querySelector(sel);
            return { scrollHeight: e.scrollHeight, clientHeight: e.clientHeight, overflowY: getComputedStyle(e).overflowY };
          };
          const top = vv.offsetTop;
          return {
            open: drawer.classList.contains('mud-drawer--open'),
            screenHeight: vv.height,
            firstTop: first.getBoundingClientRect().top - top,
            firstBottom: first.getBoundingClientRect().bottom - top,
            drawerBottom: drawer.getBoundingClientRect().bottom - top,
            rail: column('.noose-nav-rail'),
            panel: column('.noose-nav-panel')
          };
        }
        """;

    [E2EFact]
    public async Task App_bar_fits_375px()
    {
        var page = await fixture.NewPhonePageAsync(Width, Height);
        try
        {
            await RecordLinks.OpenAsync(page, "/dashboard");

            var bar = await page.EvaluateAsync<JsonElement>(AppBarScript);
            var outside = bar.GetProperty("outside").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.True(outside.Count == 0, $"App-Leiste ragt über den Rand: {string.Join(", ", outside)}");

            var labels = bar.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("Suchen und springen", labels);
            Assert.Contains("Profilmenü", labels);
            Assert.DoesNotContain("Abmelden", labels); // moved into the avatar menu
            Assert.False(await page.Locator(".mud-appbar >> text=F.I.B.").IsVisibleAsync(), "Schriftzug F.I.B. sichtbar");

            await page.Locator(".mud-appbar button[aria-label='Profilmenü']").ClickAsync();
            await Assertions.Expect(page.GetByText("Mein Profil", new PageGetByTextOptions { Exact = true })).ToBeVisibleAsync();
            await Assertions.Expect(page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Abmelden" })).ToBeVisibleAsync();
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [E2ETheory]
    [InlineData("/dashboard")]
    [InlineData("/personen")]
    public async Task Drawer_stays_reachable_on_long_page(string route)
    {
        var page = await fixture.NewPhonePageAsync(Width, Height);
        try
        {
            await RecordLinks.OpenAsync(page, route);
            await page.EvaluateAsync("() => window.scrollTo(0, document.scrollingElement.scrollHeight)");
            await page.Mouse.WheelAsync(0, 5000);
            await page.WaitForTimeoutAsync(300);

            await page.Locator(MenuButton).ClickAsync();
            await page.WaitForTimeoutAsync(600);

            var d = await page.EvaluateAsync<JsonElement>(DrawerScript);
            Assert.True(d.GetProperty("open").GetBoolean(), $"{route}: Menü geht nicht auf");

            var screenHeight = d.GetProperty("screenHeight").GetDouble();
            var firstTop = d.GetProperty("firstTop").GetDouble();
            Assert.True(firstTop >= 0 && d.GetProperty("firstBottom").GetDouble() <= screenHeight,
                $"{route}: erstes Bereichs-Symbol nicht im Bild (oben bei {firstTop:0.#} px)");

            var drawerBottom = d.GetProperty("drawerBottom").GetDouble();
            Assert.True(drawerBottom <= screenHeight + 1,
                $"{route}: Menü endet bei {drawerBottom:0.#} px, der Bildschirm bei {screenHeight:0.#} px");

            foreach (var name in new[] { "rail", "panel" })
            {
                var column = d.GetProperty(name);
                if (column.GetProperty("scrollHeight").GetInt32() > column.GetProperty("clientHeight").GetInt32())
                {
                    Assert.Contains(column.GetProperty("overflowY").GetString(), new[] { "auto", "scroll" });
                }
            }
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [E2EFact]
    public async Task Drawer_closes_after_tapping_an_entry()
    {
        var page = await fixture.NewPhonePageAsync(Width, Height);
        try
        {
            await RecordLinks.OpenAsync(page, "/dashboard");
            await page.Locator(MenuButton).ClickAsync();
            await page.WaitForTimeoutAsync(600);

            await page.Locator(".noose-nav-panel a[href]:not([href$='/dashboard']) >> visible=true").First.ClickAsync();
            await page.WaitForTimeoutAsync(1500);

            var open = await page.EvaluateAsync<bool>(
                "() => document.querySelector('.noose-drawer').classList.contains('mud-drawer--open')");
            Assert.False(open, "Menü bleibt nach dem Tippen auf einen Eintrag offen");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
