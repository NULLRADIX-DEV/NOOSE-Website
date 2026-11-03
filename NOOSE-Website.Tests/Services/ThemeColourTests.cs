using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using MudBlazor;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Theme;

namespace NOOSE_Website.Tests.Services;

/// <summary>The charcoal + bronze colour scheme: theme values, chart palettes and the places that must mirror each other.</summary>
public class ThemeColourTests
{
    private static string AppRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    private static string Read(params string[] relative)
        => File.ReadAllText(Path.Combine([AppRoot(), .. relative]));

    private static string Hex(MudBlazor.Utilities.MudColor colour) => colour.ToString(MudBlazor.Utilities.MudColorOutputFormats.Hex).ToUpperInvariant();

    // MudBlazor keeps the derived tones (Lighten/Darken) as text in its own format, so both sides go through MudColor
    private static string Hex(string colour) => Hex(new MudBlazor.Utilities.MudColor(colour));

    private static (double R, double G, double B) Rgb(string hex)
        => (int.Parse(hex[1..3], NumberStyles.HexNumber) / 255.0,
            int.Parse(hex[3..5], NumberStyles.HexNumber) / 255.0,
            int.Parse(hex[5..7], NumberStyles.HexNumber) / 255.0);

    private static double Luminance(string hex)
    {
        static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        var (r, g, b) = Rgb(hex);
        return 0.2126 * Lin(r) + 0.7152 * Lin(g) + 0.0722 * Lin(b);
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>HSL hue in degrees; 0 for a gray.</summary>
    private static double Hue(string hex)
    {
        var (r, g, b) = Rgb(hex);
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max - min < 1e-9)
        {
            return 0;
        }
        var d = max - min;
        var h = max == r ? ((g - b) / d) % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h * 60 + 360) % 360;
    }

    // ---- theme ----

    [Fact]
    public void The_default_accent_is_bronze_on_a_charcoal_background()
    {
        var dark = NooseTheme.Theme.PaletteDark;

        Assert.Equal("#71614A", NooseTheme.DefaultPrimary);
        Assert.Equal("#71614A", Hex(dark.Primary));
        Assert.Equal("#1C1C1C", Hex(dark.Background));
        Assert.Equal("#FFFFFF", Hex(dark.PrimaryContrastText));
    }

    [Fact]
    public void Text_on_the_primary_button_is_readable_and_the_bronze_alone_is_too_weak_as_text()
    {
        Assert.True(Contrast("#FFFFFF", NooseTheme.DefaultPrimary) >= 4.5);
        // why app.css reads the lighter tone for links, mentions and active navigation
        Assert.True(Contrast(NooseTheme.DefaultPrimary, "#1C1C1C") < 3);
        Assert.True(Contrast(NooseTheme.AccentOnDark, "#1C1C1C") >= 7);
        Assert.True(Contrast(NooseTheme.AccentOnDark, "#242424") >= 6);
        Assert.Equal(NooseTheme.AccentOnDark, Hex(NooseTheme.Theme.PaletteDark.PrimaryLighten), ignoreCase: true);
    }

    [Fact]
    public void An_overridden_accent_does_not_keep_the_bronze_text_tone()
    {
        var custom = NooseTheme.WithColours("#336699", null, null).PaletteDark;
        var standard = NooseTheme.WithColours(null, null, null).PaletteDark;

        Assert.Equal("#336699", Hex(custom.Primary));
        Assert.NotEqual(NooseTheme.AccentOnDark, Hex(custom.PrimaryLighten));
        Assert.Equal(NooseTheme.DefaultPrimary, Hex(standard.Primary), ignoreCase: true);
        Assert.Equal(NooseTheme.AccentOnDark, Hex(standard.PrimaryLighten), ignoreCase: true);
        Assert.Equal(NooseTheme.DefaultTertiary, Hex(standard.Tertiary), ignoreCase: true);
    }

    [Fact]
    public void The_light_palette_for_the_print_layout_is_bronze_too()
    {
        var light = NooseTheme.Theme.PaletteLight;

        Assert.Equal(NooseTheme.DefaultPrimary, Hex(light.Primary), ignoreCase: true);
        Assert.Equal("#FFFFFF", Hex(light.PrimaryContrastText));
    }

    [Fact]
    public void The_dark_palette_is_neutral_gray_not_blue_gray()
    {
        var dark = NooseTheme.Theme.PaletteDark;
        string[] neutrals =
        [
            Hex(dark.BackgroundGray), Hex(dark.Surface), Hex(dark.AppbarBackground), Hex(dark.DrawerBackground),
            Hex(dark.Divider), Hex(dark.LinesInputs), Hex(dark.TableLines), Hex(dark.TableStriped), Hex(dark.TableHover),
        ];

        foreach (var hex in neutrals)
        {
            var (r, g, b) = Rgb(hex);
            Assert.True(Math.Abs(r - b) < 0.01, $"{hex} has a colour cast");
            Assert.True(Math.Abs(r - g) < 0.01, $"{hex} has a colour cast");
        }
    }

    // ---- chart palettes ----

    [Fact]
    public void The_chart_surface_and_lines_match_the_theme()
    {
        var dark = NooseTheme.Theme.PaletteDark;

        Assert.Equal(Hex(dark.Surface), ChartPalette.Surface.ToUpperInvariant());
        Assert.Equal(Hex(dark.Divider), ChartPalette.Gridline.ToUpperInvariant());
        Assert.Equal(Hex(dark.LinesInputs), ChartPalette.DivergingNeutral.ToUpperInvariant());
    }

    [Fact]
    public void Categorical_series_are_eight_distinct_hexes_without_blue_cyan_indigo_or_teal()
    {
        Assert.Equal(8, ChartPalette.Categorical.Length);
        Assert.Equal(8, ChartPalette.Categorical.Select(c => c.ToUpperInvariant()).Distinct().Count());
        Assert.All(ChartPalette.Categorical, c => Assert.Matches("^#[0-9a-fA-F]{6}$", c));
        // HSL hues 170-260 are teal, cyan, blue and indigo
        Assert.All(ChartPalette.Categorical, c => Assert.False(Hue(c) is >= 170 and <= 260, $"{c} is blue-ish"));
        // bronze first
        Assert.InRange(Hue(ChartPalette.Categorical[0]), 30, 50);
    }

    [Fact]
    public void Every_series_colour_stays_visible_on_the_chart_surface()
        => Assert.All(ChartPalette.Categorical, c => Assert.True(Contrast(c, ChartPalette.Surface) >= 3, c));

    [Fact]
    public void The_sequential_ramp_gets_brighter_every_step_and_ends_on_sand()
    {
        var steps = ChartPalette.Sequential.Select(Luminance).ToList();

        Assert.Equal(6, steps.Count);
        Assert.Equal(steps.OrderBy(x => x), steps);
        Assert.Equal("#E8DCC4", ChartPalette.Sequential[^1], ignoreCase: true);
        // the darkest step must still read against the surface a zero cell shows
        Assert.True(Contrast(ChartPalette.Sequential[0], ChartPalette.Surface) >= 2);
    }

    [Fact]
    public void The_diverging_scale_runs_cool_neutral_warm_and_its_cool_arm_is_not_blue()
    {
        Assert.Equal(7, ChartPalette.Diverging.Length);
        Assert.Equal(ChartPalette.DivergingCool, ChartPalette.Diverging[0]);
        Assert.Equal(ChartPalette.DivergingNeutral, ChartPalette.Diverging[3]);
        Assert.Equal(ChartPalette.DivergingWarm, ChartPalette.Diverging[^1]);
        Assert.All(ChartPalette.Diverging.Take(3), c => Assert.False(Hue(c) is >= 170 and <= 260, $"{c} is blue-ish"));
    }

    [Fact]
    public void The_hazard_scale_has_five_distinct_steps_and_none_of_them_is_cyan()
    {
        Assert.Equal(5, ChartPalette.Hazard.Length);
        Assert.Equal(5, ChartPalette.Hazard.Select(c => c.ToUpperInvariant()).Distinct().Count());
        Assert.All(ChartPalette.Hazard, c => Assert.False(Hue(c) is >= 170 and <= 260, $"{c} is blue-ish"));
    }

    // ---- mirrored literals ----

    private static Dictionary<string, string> Block(string text, string start, string pattern)
    {
        var from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"{start} not found");
        var to = text.IndexOf("};", from, StringComparison.Ordinal);
        return Regex.Matches(text[from..to], pattern).ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());
    }

    [Fact]
    public void The_calendar_colours_in_csharp_and_javascript_agree()
    {
        var js = Block(Read("wwwroot", "js", "kalender.js"), "const QUELLE_FARBE", @"(\d+): '(#[0-9A-Fa-f]{6})'");

        foreach (var source in Enum.GetValues<CalendarSource>())
        {
            Assert.True(js.TryGetValue(((int)source).ToString(CultureInfo.InvariantCulture), out var hex), $"{source} missing in kalender.js");
            Assert.Equal(CalendarDisplay.Colour(source).ToUpperInvariant(), hex);
        }
    }

    [Fact]
    public void The_graph_legend_mirrors_the_node_colours_in_the_script()
    {
        var js = Block(Read("wwwroot", "js", "graph.js"), "const TYP_FARBE", @"(\w+): '(#[0-9A-Fa-f]{6})'");
        var legend = Regex.Matches(Read("Components", "Pages", "Graph", "RelationGraph.razor"), @"new\(nameof\((\w+)\), ""[^""]+"", ""(#[0-9A-Fa-f]{6})""\)")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value.ToUpperInvariant());

        Assert.Equal(12, legend.Count);
        foreach (var (type, hex) in legend)
        {
            Assert.True(js.TryGetValue(type, out var scripted), $"{type} missing in graph.js");
            Assert.Equal(hex, scripted);
        }
        // the focus ring stays the one hue no node type uses
        Assert.DoesNotContain("#F778BA", js.Values);
        Assert.All(js.Values, c => Assert.False(Hue(c) is >= 170 and <= 260, $"{c} is blue-ish"));
    }

    // ---- leftovers of the old scheme ----

    private static readonly string[] OldLiterals =
    [
        "#22D3EE", "#0E1116", "#161B22", "#E6EDF3", "#9BA8B8", "#8B98A8", "#7C8CF8", "#58A6FF", "#2DD4BF",
        "#0B0E13", "#0F141A", "#11161D", "#222B36", "#2B3744", "#1E2731",
    ];

    [Theory]
    [InlineData("wwwroot/app.css")]
    [InlineData("wwwroot/js/kalender.js")]
    [InlineData("wwwroot/js/graph.js")]
    [InlineData("wwwroot/js/statistik-charts.js")]
    [InlineData("Components/Layout/ReconnectModal.razor.css")]
    [InlineData("Components/Pages/Presentations/Shared/SlideView.razor.css")]
    [InlineData("Components/Layout/PublicLayout.razor")]
    [InlineData("Components/Layout/PublicSiteLayout.razor")]
    [InlineData("Components/Layout/LegalLayout.razor")]
    [InlineData("Components/Layout/BuergerLayout.razor")]
    [InlineData("Components/Layout/ApplicantPortalLayout.razor")]
    [InlineData("Components/Account/Shared/AccountLayout.razor")]
    [InlineData("Components/Pages/Home.razor")]
    [InlineData("Components/Pages/Monitoring/Shared/AccessHeatmap.razor")]
    [InlineData("Components/Common/Shared/Charts/EChartsCanvas.razor")]
    public void Colour_sources_hold_no_literal_of_the_old_blue_scheme(string path)
    {
        var text = Read(path.Split('/'));

        Assert.All(OldLiterals, old => Assert.DoesNotContain(old, text, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotMatch(@"rgba\(\s*34\s*,\s*211\s*,\s*238", text);
    }

    [Fact]
    public void No_stylesheet_or_component_reads_the_misspelled_background_variable()
    {
        // MudBlazor only defines --mud-palette-background-gray; the -grey spelling resolves to nothing
        var root = AppRoot();
        var hits = new[] { "*.razor", "*.css", "*.js" }
            .SelectMany(pattern => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Where(f => Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar) is var parts
                        && parts.All(p => p is not ("bin" or "obj" or "lib")))
            .Where(f => File.ReadAllText(f).Contains("--mud-palette-background-grey", StringComparison.Ordinal))
            .ToList();
        Assert.Empty(hits);
    }
}
