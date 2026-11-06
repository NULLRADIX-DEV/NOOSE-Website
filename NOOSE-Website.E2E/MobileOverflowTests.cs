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
            await RecordLinks.OpenAsync(page, route);
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
            await RecordLinks.OpenAsync(page, listRoute);
            var detail = await RecordLinks.FindDetailPathAsync(page, linkPrefix);
            // the demo data leaves some lists empty; such a case reports as skipped instead of failing forever
            Skip.If(detail is null, $"{listRoute}: no link or row leads to {linkPrefix}… (no such record in the demo data)");
            await RecordLinks.OpenAsync(page, detail!);
            await AssertFitsAsync(page, detail!);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task AssertFitsAsync(IPage page, string route)
    {
        var r = await OverflowProbe.MeasureAsync(page);
        if (!r.Fits)
            await SaveScreenshotAsync(page, route);
        Assert.True(r.ScrollWidth <= r.ViewportWidth, $"{route}: {r.ScrollWidth}px – {string.Join("; ", r.Offenders)}");
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
