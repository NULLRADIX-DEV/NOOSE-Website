using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>Every page must fit a 375 px phone without sideways scrolling (as the demo Director sees it).</summary>
[Collection(E2ECollection.Name)]
public sealed class MobileOverflowTests(E2EFixture fixture)
{
    private const int Width = 375;
    private const int Height = 812;

    public static TheoryData<string> StaticRoutes()
    {
        var data = new TheoryData<string>();
        foreach (var route in RouteCatalog.StaticRoutes())
            data.Add(route);
        return data;
    }

    public static TheoryData<string, string> DetailSeeds()
    {
        var data = new TheoryData<string, string>();
        foreach (var (list, prefix) in RouteCatalog.DetailSeeds)
            data.Add(list, prefix);
        return data;
    }

    [E2ETheory]
    [MemberData(nameof(StaticRoutes))]
    public async Task Every_static_route_fits_375px(string route)
    {
        var page = await fixture.NewPageAsync(Width, Height);
        try
        {
            await OpenAsync(page, route);
            await AssertFitsAsync(page, route);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    [E2ETheory]
    [MemberData(nameof(DetailSeeds))]
    public async Task Record_detail_fits_375px(string listRoute, string linkPrefix)
    {
        var page = await fixture.NewPageAsync(Width, Height);
        try
        {
            await OpenAsync(page, listRoute);
            var detail = await FindDetailPathAsync(page, linkPrefix);
            // the demo data leaves some lists empty; such a case reports as skipped instead of failing forever
            Skip.If(detail is null, $"{listRoute}: no link or row leads to {linkPrefix}… (no such record in the demo data)");
            await OpenAsync(page, detail!);
            await AssertFitsAsync(page, detail!);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task OpenAsync(IPage page, string route)
    {
        await page.GotoAsync(route, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 60_000 });
        await page.WaitForTimeoutAsync(1500);
    }

    private static async Task AssertFitsAsync(IPage page, string route)
    {
        var r = await OverflowProbe.MeasureAsync(page);
        if (!r.Fits)
            await SaveScreenshotAsync(page, route);
        Assert.True(r.ScrollWidth <= r.ViewportWidth, $"{route}: {r.ScrollWidth}px – {string.Join("; ", r.Offenders)}");
    }

    // Clickable things in the page content that are no link or control: table rows and cards/list items with an
    // @onclick (cursor:pointer). Marked with data-e2e-candidate so the test can click them one by one.
    private const string MarkCandidatesScript = """
        () => {
          const root = document.querySelector('.mud-main-content') || document.body;
          const skip = 'a, button, input, select, textarea, label, [role=button], [role=tab], [role=menuitem], .mud-input-control, .mud-chip, nav, header, .mud-table-head, .mud-table-pagination';
          const found = [];
          for (const el of root.querySelectorAll('tbody tr.mud-table-row, [style*="cursor"], .cursor-pointer, .mud-list-item-clickable')) {
            if (el.closest(skip)) continue;
            if (getComputedStyle(el).cursor !== 'pointer' && !el.matches('tbody tr.mud-table-row')) continue;
            const r = el.getBoundingClientRect();
            if (r.width === 0 || r.height === 0) continue;
            if (found.some(f => f.contains(el) || el.contains(f))) continue;
            found.push(el);
            if (found.length >= 6) break;
          }
          found.forEach((el, i) => el.setAttribute('data-e2e-candidate', String(i)));
          return found.length;
        }
        """;

    /// <summary>
    /// First anchor to prefix + one segment; failing that, the first clickable row or card in the content that
    /// navigates there (many lists open a record with @onclick instead of a link).
    /// </summary>
    private static async Task<string?> FindDetailPathAsync(IPage page, string prefix)
    {
        var listUrl = page.Url;
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var hrefs = await page.EvaluateAsync<string[]>(
                "() => [...document.querySelectorAll('a[href]')].map(a => a.pathname)");
            var hit = hrefs.FirstOrDefault(h => RouteCatalog.IsDetailPath(h, prefix));
            if (hit is not null)
                return hit;
            await page.WaitForTimeoutAsync(500);
        }

        var count = await page.EvaluateAsync<int>(MarkCandidatesScript);
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                // back to a fresh list: the previous click may have opened a dialog or navigated elsewhere
                await OpenAsync(page, listUrl);
                if (await page.EvaluateAsync<int>(MarkCandidatesScript) <= i)
                    return null;
            }
            var before = new Uri(page.Url).AbsolutePath;
            try
            {
                await page.Locator($"[data-e2e-candidate='{i}']").ClickAsync(new LocatorClickOptions { Timeout = 5_000 });
                await page.WaitForURLAsync(u => new Uri(u).AbsolutePath != before, new PageWaitForURLOptions { Timeout = 5_000 });
            }
            catch (Exception e) when (e is TimeoutException or PlaywrightException)
            {
                continue;
            }
            var path = new Uri(page.Url).AbsolutePath;
            if (RouteCatalog.IsDetailPath(path, prefix))
                return path;
        }
        return null;
    }

    private static async Task SaveScreenshotAsync(IPage page, string route)
    {
        try
        {
            Directory.CreateDirectory(E2EFixture.ArtifactsDirectory);
            var name = route.Trim('/').Replace('/', '_');
            if (name.Length == 0)
                name = "root";
            var file = Path.Combine(E2EFixture.ArtifactsDirectory, $"overflow-{Width}-{name}.png");
            await page.ScreenshotAsync(new PageScreenshotOptions { Path = file, FullPage = true });
        }
        catch (PlaywrightException)
        {
            // a missing screenshot must not hide the real assertion
        }
    }
}
