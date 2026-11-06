using System.Text.Json;
using Microsoft.Playwright;

namespace NOOSE_Website.E2E;

/// <summary>Result of one overflow measurement: page width, viewport width and the elements that stick out.</summary>
public sealed record OverflowResult(int ScrollWidth, int ViewportWidth, IReadOnlyList<string> Offenders)
{
    public bool Fits => ScrollWidth <= ViewportWidth;
}

/// <summary>
/// Measures horizontal overflow the same way as the live check of 06.11.2026: every element whose
/// getBoundingClientRect().right exceeds the viewport by more than 2 px, only the outermost one per branch.
/// Elements cut off by a horizontally clipping ancestor (overflow hidden/auto/scroll/clip) that itself fits are left
/// out, they do not widen the page.
/// </summary>
public static class OverflowProbe
{
    private const string Script = """
        () => {
          const vw = document.documentElement.clientWidth;
          const clipped = (el) => {
            for (let p = el.parentElement; p && p !== document.body; p = p.parentElement) {
              const ox = getComputedStyle(p).overflowX;
              if (ox !== 'visible') return p.getBoundingClientRect().right <= vw + 2;
            }
            return false;
          };
          const describe = (el) => {
            let s = el.tagName.toLowerCase();
            if (el.id) s += '#' + el.id;
            const cls = [...el.classList].filter(c => !c.startsWith('b-')).slice(0, 4);
            if (cls.length) s += '.' + cls.join('.');
            const text = (el.innerText || '').trim().replace(/\s+/g, ' ').slice(0, 40);
            if (text) s += ' "' + text + '"';
            return s + ' right=' + Math.round(el.getBoundingClientRect().right);
          };
          const out = [];
          for (const el of document.body.querySelectorAll('*')) {
            const r = el.getBoundingClientRect();
            if (r.width === 0 && r.height === 0) continue;
            if (r.right <= vw + 2) continue;
            if (out.some(o => o.contains(el))) continue;
            if (getComputedStyle(el).position === 'fixed') continue;
            if (clipped(el)) continue;
            out.push(el);
          }
          return {
            scrollWidth: document.documentElement.scrollWidth,
            viewportWidth: vw,
            offenders: out.slice(0, 15).map(describe),
          };
        }
        """;

    public static async Task<OverflowResult> MeasureAsync(IPage page)
    {
        var raw = await page.EvaluateAsync<JsonElement>(Script);
        var offenders = raw.GetProperty("offenders").EnumerateArray().Select(e => e.GetString() ?? "").ToList();
        return new OverflowResult(raw.GetProperty("scrollWidth").GetInt32(), raw.GetProperty("viewportWidth").GetInt32(), offenders);
    }
}
