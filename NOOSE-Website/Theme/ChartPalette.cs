using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Theme;

/// <summary>Single source of truth for chart colours; every value validated against the dark surface #242424.</summary>
/// <remarks>
/// Each palette does exactly one job. Never mix them: a status colour must not stand in for a series,
/// and a series colour must not imply "good" or "critical".
///
/// The theme accent colours are deliberately NOT used as series colours. The bronze accent #71614A is
/// far below the chroma floor (OKLCH C ~0.05 vs 0.10) and only 2.6:1 on the surface, so it reads as
/// gray; the palettes below carry the same family as a brighter bronze, plus hues stepped into the usable
/// lightness band (OKLCH L ~0.48-0.67), then checked for colour-blind separation. No blue, cyan,
/// indigo or teal slot exists on purpose: the house colour is warm, and the cool end of the diverging
/// scale is violet. The admin's runtime accent override stays UI chrome (buttons, rail, active states)
/// because an arbitrary hex would destroy the validated distances.
/// </remarks>
public static class ChartPalette
{
    /// <summary>Series identity. Fixed order — assign in sequence, never cycle, never generate a ninth.</summary>
    /// <remarks>
    /// bronze, violet, green, magenta, amber, red, olive, orange. Worst adjacent pair: colour-blind
    /// dE 8.2, normal-vision dE 20.8, every step at least 3.4:1 on the surface.
    /// </remarks>
    public static readonly string[] Categorical =
    [
        "#b27923", "#9a59cf", "#54aa71", "#d053a3",
        "#ad941d", "#d3434c", "#83a412", "#c4530f",
    ];

    /// <summary>Series cap for charts where any two marks can end up side by side (scatter, bubble, small multiples).</summary>
    /// <remarks>Only the first three slots survive the all-pairs check (colour-blind dE 8.1, normal 16.2); past that, fold into "Sonstige" or facet.</remarks>
    public const int AllPairsSeriesCap = 3;

    /// <summary>Magnitude, low to high. One hue family (bronze), brighter means more.</summary>
    /// <remarks>
    /// Monotone lightness with every gap above the visible-step threshold (dL >= 0.07); the darkest step is
    /// still 2.3:1 on the surface and the last step is #E8DCC4. Use for every heatmap.
    /// </remarks>
    public static readonly string[] Sequential =
    [
        "#73572c", "#8f6c38", "#ac844a", "#c59f67", "#dabc91", "#E8DCC4",
    ];

    /// <summary>Polarity around a baseline: cool arm (violet), neutral middle, warm arm (orange).</summary>
    /// <remarks>
    /// The midpoint is the theme's LinesInputs — near-gray, so it reads as "nothing". The inner steps
    /// sit under 3:1, so diverging marks always carry visible value labels. Mirrored steps of the two arms
    /// share their lightness; violet against orange stays at dE 18+ under protan and deutan vision, which a
    /// green or olive cool arm would not (dE 3 against the orange).
    /// </remarks>
    public static readonly string[] Diverging =
    [
        "#ab7acd", "#8e64ab", "#75528e", "#3F3F3F", "#9f4e03", "#be5d00", "#dc6e07",
    ];

    /// <summary>Neutral middle of <see cref="Diverging"/>; also the "no data" tint.</summary>
    public const string DivergingNeutral = "#3F3F3F";

    /// <summary>Cool (negative / falling) end of the diverging scale.</summary>
    public const string DivergingCool = "#ab7acd";

    /// <summary>Warm (positive / rising) end of the diverging scale.</summary>
    public const string DivergingWarm = "#dc6e07";

    /// <summary>Hazard heat, indexed by <see cref="HazardLevel"/>. Reserved meaning — never a series colour.</summary>
    /// <remarks>
    /// One scale for the graph, calendar and hazard lists: none gray, low green, medium yellow, high amber,
    /// critical red. Medium is yellow, the one hue between green and amber that still clears dE 7.7
    /// (colour-blind) against its neighbours.
    /// </remarks>
    public static readonly string[] Hazard =
    [
        "#A8A29A", "#3FB950", "#E0C341", "#D29922", "#F85149",
    ];

    /// <summary>De-emphasis gray for context marks behind an emphasised series (3.4:1 on the surface).</summary>
    public const string Muted = "#7A756C";

    /// <summary>Chart surface; also what a zero heatmap cell shows instead of the lightest ramp step. Matches PaletteDark.Surface.</summary>
    public const string Surface = "#242424";

    /// <summary>Hairline for gridlines, axes and cell borders. Matches PaletteDark.Divider.</summary>
    public const string Gridline = "#333333";

    /// <summary>Hazard colour for a level, safe against out-of-range values.</summary>
    public static string ForHazard(HazardLevel level)
    {
        var i = (int)level;
        return i >= 0 && i < Hazard.Length ? Hazard[i] : Hazard[0];
    }

    /// <summary>Hazard colour for a raw score, using the same thresholds as the hazard bands.</summary>
    public static string ForScore(int? score) => ForHazard(HazardLevelLogic.From(score));

    /// <summary>First <paramref name="count"/> categorical slots; order is stable so a filter never repaints survivors.</summary>
    public static string[] Series(int count)
    {
        if (count <= 0)
        {
            return [];
        }
        // A ninth series would need a generated hue, which no colour-blind check can pass
        return Categorical.Take(Math.Min(count, Categorical.Length)).ToArray();
    }

    /// <summary>Ordinal ramp of <paramref name="count"/> steps for an ordered scale (bands, funnel stages, tiers).</summary>
    /// <remarks>Spreads across the sequential ramp so the reader sees the order in the colour itself.</remarks>
    public static string[] Ordinal(int count)
    {
        if (count <= 0)
        {
            return [];
        }
        if (count == 1)
        {
            return [Sequential[^1]];
        }
        var steps = new string[count];
        for (var i = 0; i < count; i++)
        {
            // walk the whole ramp so the endpoints are always the extremes
            var position = (double)i / (count - 1) * (Sequential.Length - 1);
            steps[i] = Sequential[(int)Math.Round(position)];
        }
        return steps;
    }

    /// <summary>Sequential step for a value in [0, max]; returns null at zero so the cell stays surface-coloured.</summary>
    public static string? ForMagnitude(int value, int max)
    {
        if (value <= 0 || max <= 0)
        {
            return null;
        }
        var position = (double)value / max * (Sequential.Length - 1);
        return Sequential[Math.Clamp((int)Math.Ceiling(position), 0, Sequential.Length - 1)];
    }
}
