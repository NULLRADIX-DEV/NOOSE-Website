using System.Text.Json;
using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>The page header on a 375 px phone: secondary actions in the ⋮ menu, from the very first paint on.</summary>
[Collection(E2ECollection.Name)]
public sealed class HeaderTests(E2EFixture fixture)
{
    private const int Width = 375;
    private const int Height = 812;

    // HeaderMenu's MudMenu; display:none from 960 px up
    private const string MenuButton = ".mud-main-content .noose-kopfmenue-knopf button >> visible=true";

    // The header row and its visible controls, measured against the screen.
    private const string HeaderScript = """
        () => {
          const row = document.querySelector('.mud-main-content .noose-kopf-umbruch');
          if (!row) return null;
          const vv = window.visualViewport;
          const visible = e => { const r = e.getBoundingClientRect(); return r.width > 0 && r.height > 0; };
          const controls = [...row.querySelectorAll('button, a, summary')].filter(visible);
          return {
            screenWidth: vv ? vv.width : window.innerWidth,
            rowRight: row.getBoundingClientRect().right,
            outside: controls
              .filter(e => e.getBoundingClientRect().right > (vv ? vv.width : window.innerWidth) + 0.5)
              .map(e => (e.getAttribute('aria-label') || e.textContent || e.tagName).trim()),
            labels: controls.map(e => (e.getAttribute('aria-label') || e.textContent || '').trim())
          };
        }
        """;

    /// <summary>
    /// The prerendered page, before any script runs, already has the narrow header: Blazor's script is blocked, so
    /// what the test sees is exactly the first paint, and nothing may wait for a width check on the circuit.
    /// </summary>
    [E2EFact]
    public async Task Header_is_narrow_on_first_paint()
    {
        var page = await fixture.NewPhonePageAsync(Width, Height);
        try
        {
            await page.RouteAsync("**/_framework/blazor.web.js*", route => route.AbortAsync());
            await page.GotoAsync("/personen", new PageGotoOptions { WaitUntil = WaitUntilState.Commit, Timeout = 60_000 });
            await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded);

            var header = await page.EvaluateAsync<JsonElement>(HeaderScript);
            Assert.True(header.ValueKind == JsonValueKind.Object, "Seitenkopf (.noose-kopf-umbruch) fehlt in der ersten Auslieferung");

            var outside = header.GetProperty("outside").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.True(outside.Count == 0, $"Seitenkopf ragt über den Rand: {string.Join(", ", outside)}");

            var labels = header.GetProperty("labels").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.DoesNotContain("CSV", labels);
            Assert.True(await page.Locator(MenuButton).IsVisibleAsync(), "⋮-Knopf fehlt in der ersten Auslieferung");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [E2EFact]
    public async Task Header_menu_holds_secondary_actions()
    {
        var page = await fixture.NewPhonePageAsync(Width, Height);
        try
        {
            await RecordLinks.OpenAsync(page, "/personen");

            var inlineCsv = page.Locator(".mud-main-content .noose-kopf-umbruch button:has-text('CSV')");
            Assert.False(await inlineCsv.IsVisibleAsync(), "CSV steht bei 375 px noch in der Kopfzeile");

            await page.Locator(MenuButton).ClickAsync();
            var entry = page.Locator(".mud-popover-open .mud-menu-item:has-text('CSV')");
            await Assertions.Expect(entry).ToBeVisibleAsync();

            // the Director sees today's leadership buttons in the menu, behind the same check
            await Assertions.Expect(page.Locator(".mud-popover-open .mud-menu-item:has-text('Zusammenführen')")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator(".mud-popover-open .mud-menu-item:has-text('Papierkorb')")).ToBeVisibleAsync();
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    /// <summary>From 960 px up the header shows every button in its row and no ⋮ menu.</summary>
    [E2EFact]
    public async Task Wide_header_keeps_its_buttons()
    {
        var page = await fixture.NewPageAsync(1280, 900);
        try
        {
            await RecordLinks.OpenAsync(page, "/personen");

            var row = page.Locator(".mud-main-content .noose-kopf-umbruch");
            await Assertions.Expect(row.Locator("button:has-text('CSV')")).ToBeVisibleAsync();
            await Assertions.Expect(row.Locator("button:has-text('Zusammenführen')")).ToBeVisibleAsync();
            Assert.False(await page.Locator(MenuButton).IsVisibleAsync(), "⋮-Menü ist ab 960 px sichtbar");
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }
}
