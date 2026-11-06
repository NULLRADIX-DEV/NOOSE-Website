using System.Text.Json;
using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>
/// At 1280 px the branch must render like master: the same routes are screenshotted on both apps (both in demo mode)
/// and compared pixel by pixel. Nondeterministic regions (times, dates, "vor X Minuten", the build version) are
/// masked on both sides instead of raising the tolerance.
/// </summary>
[Collection(E2ECollection.Name)]
public sealed class DesktopBaselineTests(E2EFixture fixture)
{
    private const int Width = 1280;
    private const int Height = 900;

    /// <summary>Share of differing pixels that still counts as equal (0.1 %).</summary>
    private const double Tolerance = 0.001;

    /// <summary>A pixel differs when one colour channel differs by more than this (rounding noise of the rasteriser).</summary>
    private const int ChannelThreshold = 2;

    /// <summary>
    /// Name of a case → how to reach it. A seed (list route, link prefix) is resolved on each app separately, the ids
    /// of the demo records differ between the two databases; <c>Suffix</c> is appended to the record path.
    /// <c>SortBy</c> clicks that grid column header first (on the seed list and on the shot route): the demo records
    /// are inserted in one batch with the same CreatedAt, so lists sorted by date or by a score with ties come back in
    /// database order, which differs between two databases. The case numbers are deterministic.
    /// <c>Masks</c> are extra CSS selectors masked on both sides (regions whose order depends on such ties).
    /// </summary>
    private sealed record Target(string? Route = null, string? SeedList = null, string? SeedPrefix = null, string Suffix = "",
        bool Print = false, string? SortBy = null, string[]? Masks = null);

    private static readonly Dictionary<string, Target> _targets = new(StringComparer.Ordinal)
    {
        // "Letzte Änderungen": entries seeded with the same timestamp come in database order
        ["/dashboard"] = new("/dashboard", Masks: [".mud-timeline"]),
        ["/personen"] = new("/personen", SortBy: "Aktenzeichen"),
        ["/personen/{id}"] = new(SeedList: "/personen", SeedPrefix: "/personen/", SortBy: "Aktenzeichen"),
        ["/fraktionen"] = new("/fraktionen", SortBy: "Aktenzeichen"),
        ["/einstellungen"] = new("/einstellungen"),
        ["/aufgaben"] = new("/aufgaben"),
        ["/kasse"] = new("/kasse"),
        ["/"] = new("/"),
        ["/gesucht"] = new("/gesucht"),
        ["/portal"] = new("/portal"),
        // print views with print media emulation (Review Focus 5)
        ["/personen/{id}/druck"] = new(SeedList: "/personen", SeedPrefix: "/personen/", Suffix: "/druck", Print: true,
            SortBy: "Aktenzeichen"),
        ["/urkunden/ehrung/{id}"] = new(SeedList: "/profil", SeedPrefix: "/urkunden/ehrung/", Print: true),
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var name in _targets.Keys)
            data.Add(name);
        return data;
    }

    [E2EBaselineTheory]
    [MemberData(nameof(Cases))]
    public async Task Desktop_matches_master(string name)
    {
        var target = _targets[name];
        // one instant for both browsers, so client-side clocks render the same
        var now = DateTime.UtcNow;
        var master = await ShootAsync(E2EFixture.BaselineUrl!, target, now);
        var branch = await ShootAsync(E2EFixture.BaseUrl!, target, now);
        Skip.If(master.Path is null || branch.Path is null,
            $"{name}: no record found ({target.SeedList} → {target.SeedPrefix}…, master: {master.Path ?? "none"}, branch: {branch.Path ?? "none"})");

        var result = await CompareAsync(master.Png!, branch.Png!);
        var share = result.Total == 0 ? 0 : (double)result.Different / result.Total;
        var equal = result.SameSize && share <= Tolerance;
        if (!equal)
            await SaveAsync(name, master.Png!, branch.Png!, result.DiffPng);

        Assert.True(result.SameSize,
            $"{name}: size differs – master {result.MasterWidth}×{result.MasterHeight}, branch {result.BranchWidth}×{result.BranchHeight} ({branch.Path})");
        Assert.True(share <= Tolerance,
            $"{name}: {result.Different} of {result.Total} pixels differ ({share:P3}, allowed {Tolerance:P1}) ({branch.Path})");
    }

    private sealed record Shot(string? Path, byte[]? Png);

    private async Task<Shot> ShootAsync(string baseUrl, Target target, DateTime now)
    {
        var page = await fixture.NewPageAsync(Width, Height, baseUrl);
        try
        {
            await page.Clock.SetFixedTimeAsync(now);
            string path;
            if (target.Route is not null)
            {
                path = target.Route;
            }
            else
            {
                await RecordLinks.OpenAsync(page, target.SeedList!);
                if (target.SortBy is not null)
                    await SortByAsync(page, target.SortBy);
                var record = await RecordLinks.FindDetailPathAsync(page, target.SeedPrefix!);
                if (record is null)
                    return new Shot(null, null);
                path = record + target.Suffix;
            }

            if (target.Print)
                await page.EmulateMediaAsync(new PageEmulateMediaOptions { Media = Media.Print });
            await RecordLinks.OpenAsync(page, path);
            if (target.SortBy is not null && target.Route is not null)
                await SortByAsync(page, target.SortBy);
            await page.EvaluateAsync(MarkMasksScript);
            var mask = target.Masks is null ? "[data-e2e-mask]" : string.Join(", ", ["[data-e2e-mask]", .. target.Masks]);
            var png = await page.ScreenshotAsync(new PageScreenshotOptions
            {
                FullPage = true,
                Animations = ScreenshotAnimations.Disabled,
                Caret = ScreenshotCaret.Hide,
                Mask = [page.Locator(mask)],
            });
            return new Shot(path, png);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    /// <summary>Sorts a data grid by one column (click on its sortable header) and waits for the re-render.</summary>
    private static async Task SortByAsync(IPage page, string column)
    {
        var header = page.Locator("th").Filter(new LocatorFilterOptions { HasText = column }).Locator(".sortable-column-header").First;
        await header.ClickAsync(new LocatorClickOptions { Timeout = 10_000 });
        await page.WaitForTimeoutAsync(500);
    }

    // Marks the elements whose own text is a time, a date, a relative time or the build version: the two apps seeded
    // their demo data at different moments and are different builds. The mask covers the element box on both sides,
    // so the layout around it is still compared.
    private const string MarkMasksScript = """
        () => {
          const patterns = [
            /\b\d{1,2}:\d{2}\b/,                                   // 14:05
            /\b\d{1,2}\.\d{1,2}\.(\d{2,4})?/,                      // 06.11.2026, 06.11.
            /\b\d{4}-\d{2}-\d{2}\b/,                               // 2026-11-06
            /\bvor\s+(\d+|einer|einem|wenigen|kurzem)\b/i,         // vor 5 Minuten
            /\bin\s+\d+\s+(Sek|Min|Std|Stunde|Tag|Woche|Monat)/i,  // in 3 Tagen
            /\b(gerade eben|soeben|heute|gestern|morgen)\b/i,
            /\b\d+\s*(Sek|Min|Std|Stunden?|Tage?n?|Wochen?|Monate?n?)(?![a-zäöü])/i,  // 5 Min., 3 Tage
            /^\s*v?\d+\.\d+\.\d+(\.\d+)?([+-][0-9A-Za-z.]+)?\s*$/,  // 1.0.0+ed8e0984
          ];
          let count = 0;
          const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
          for (let node = walker.nextNode(); node; node = walker.nextNode()) {
            const text = node.textContent;
            if (!text || !text.trim()) continue;
            if (!patterns.some(p => p.test(text))) continue;
            const el = node.parentElement;
            if (!el || el.closest('script, style')) continue;
            el.setAttribute('data-e2e-mask', '');
            count++;
          }
          for (const el of document.querySelectorAll('time, [datetime]')) {
            el.setAttribute('data-e2e-mask', '');
            count++;
          }
          return count;
        }
        """;

    private sealed record Comparison(
        bool SameSize, int MasterWidth, int MasterHeight, int BranchWidth, int BranchHeight, long Total, long Different, byte[]? DiffPng);

    // Decodes both PNGs in a blank page, counts the differing pixels and draws a diff image (red = differs, the master
    // shot faded underneath). Done in the browser so the test needs no image library.
    private const string CompareScript = """
        async ([a, b, threshold, limit]) => {
          const decode = async (b64) => {
            const bytes = Uint8Array.from(atob(b64), c => c.charCodeAt(0));
            return await createImageBitmap(new Blob([bytes], { type: 'image/png' }));
          };
          const [ia, ib] = await Promise.all([decode(a), decode(b)]);
          const res = { sameSize: ia.width === ib.width && ia.height === ib.height,
                        mw: ia.width, mh: ia.height, bw: ib.width, bh: ib.height, total: 0, different: 0, diff: null };
          const w = Math.max(ia.width, ib.width), h = Math.max(ia.height, ib.height);
          const pixels = (img) => {
            const c = new OffscreenCanvas(w, h);
            const x = c.getContext('2d', { willReadFrequently: true });
            x.fillStyle = '#000'; x.fillRect(0, 0, w, h);
            x.drawImage(img, 0, 0);
            return x.getImageData(0, 0, w, h).data;
          };
          const pa = pixels(ia), pb = pixels(ib);
          const out = new ImageData(w, h);
          const od = out.data;
          let different = 0;
          for (let i = 0; i < pa.length; i += 4) {
            const d = Math.max(Math.abs(pa[i] - pb[i]), Math.abs(pa[i + 1] - pb[i + 1]), Math.abs(pa[i + 2] - pb[i + 2]));
            if (d > threshold) {
              different++;
              od[i] = 255; od[i + 1] = 0; od[i + 2] = 0;
            } else {
              const g = 255 - (255 - (pa[i] + pa[i + 1] + pa[i + 2]) / 3) * 0.25;
              od[i] = g; od[i + 1] = g; od[i + 2] = g;
            }
            od[i + 3] = 255;
          }
          res.total = w * h;
          res.different = different;
          if (!res.sameSize || different / res.total > limit) {
            const c = new OffscreenCanvas(w, h);
            c.getContext('2d').putImageData(out, 0, 0);
            const blob = await c.convertToBlob({ type: 'image/png' });
            const buf = new Uint8Array(await blob.arrayBuffer());
            let s = '';
            for (let i = 0; i < buf.length; i += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
            res.diff = btoa(s);
          }
          return res;
        }
        """;

    private async Task<Comparison> CompareAsync(byte[] master, byte[] branch)
    {
        var page = await fixture.NewPageAsync(Width, Height);
        try
        {
            var raw = await page.EvaluateAsync<JsonElement>(CompareScript,
                new object[] { Convert.ToBase64String(master), Convert.ToBase64String(branch), ChannelThreshold, Tolerance });
            var diff = raw.GetProperty("diff");
            return new Comparison(
                raw.GetProperty("sameSize").GetBoolean(),
                raw.GetProperty("mw").GetInt32(), raw.GetProperty("mh").GetInt32(),
                raw.GetProperty("bw").GetInt32(), raw.GetProperty("bh").GetInt32(),
                raw.GetProperty("total").GetInt64(), raw.GetProperty("different").GetInt64(),
                diff.ValueKind == JsonValueKind.String ? Convert.FromBase64String(diff.GetString()!) : null);
        }
        finally
        {
            await page.Context.CloseAsync();
        }
    }

    private static async Task SaveAsync(string name, byte[] master, byte[] branch, byte[]? diff)
    {
        Directory.CreateDirectory(E2EFixture.ArtifactsDirectory);
        var stem = name.Trim('/').Replace('/', '_').Replace("{", "").Replace("}", "");
        if (stem.Length == 0)
            stem = "root";
        var prefix = Path.Combine(E2EFixture.ArtifactsDirectory, $"desktop-{Width}-{stem}");
        await File.WriteAllBytesAsync($"{prefix}-master.png", master);
        await File.WriteAllBytesAsync($"{prefix}-branch.png", branch);
        if (diff is not null)
            await File.WriteAllBytesAsync($"{prefix}-diff.png", diff);
    }
}
