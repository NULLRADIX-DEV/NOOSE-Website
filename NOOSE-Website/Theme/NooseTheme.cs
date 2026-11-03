using MudBlazor;
using MudBlazor.Utilities;

namespace NOOSE_Website.Theme;

/// <summary>Dark FIB theme (charcoal + bronze); central colour/layout definition. Dark mode only; accent colours overridable in admin.</summary>
public static class NooseTheme
{
    /// <summary>Default accent colours (fallback when nothing is set in admin).</summary>
    public const string DefaultPrimary = "#71614A";
    public const string DefaultSecondary = "#3FB950";
    public const string DefaultTertiary = "#9A8F80";

    /// <summary>Light bronze for text and icons on dark: #71614A itself is only 2.85:1 on the background, this is 7.0:1.</summary>
    public const string AccentOnDark = "#B5A488";

    /// <summary>Builds the theme with optionally overridden accent colours; returns a fresh instance so per-circuit themes don't mutate the static one.</summary>
    public static MudTheme WithColours(string? primary, string? secondary, string? tertiary)
    {
        var theme = Generate();
        if (string.IsNullOrWhiteSpace(primary))
        {
            theme.PaletteDark.Primary = DefaultPrimary;
        }
        else
        {
            theme.PaletteDark.Primary = primary;
            // an arbitrary accent gets MudBlazor's derived light tone, not the bronze one
            theme.PaletteDark.PrimaryLighten = new MudColor(primary).ColorRgbLighten().ToString(MudColorOutputFormats.Hex);
        }
        theme.PaletteDark.Secondary = string.IsNullOrWhiteSpace(secondary) ? DefaultSecondary : secondary;
        theme.PaletteDark.Tertiary = string.IsNullOrWhiteSpace(tertiary) ? DefaultTertiary : tertiary;
        return theme;
    }

    public static readonly MudTheme Theme = Generate();

    private static MudTheme Generate() => new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = DefaultPrimary,
            PrimaryContrastText = "#FFFFFF",
            PrimaryLighten = AccentOnDark,
            Secondary = DefaultSecondary,
            Tertiary = DefaultTertiary,

            Info = "#A3906F",
            Success = "#3FB950",
            Warning = "#D29922",
            Error = "#F85149",
            Dark = "#101010",

            Background = "#1C1C1C",
            BackgroundGray = "#151515",
            Surface = "#242424",

            AppbarBackground = "#202020",
            AppbarText = "#ECE8E1",

            DrawerBackground = "#181818",
            DrawerText = "#CFC9BF",
            DrawerIcon = "#9A9488",

            TextPrimary = "#ECE8E1",
            TextSecondary = "#A8A29A",
            TextDisabled = "#6B6760",

            ActionDefault = "#A8A29A",
            ActionDisabled = "#555555",
            ActionDisabledBackground = "#2A2A2A",
            Divider = "#333333",
            DividerLight = "#2A2A2A",
            LinesDefault = "#333333",
            LinesInputs = "#3F3F3F",
            TableLines = "#2E2E2E",
            TableStriped = "#202020",
            TableHover = "#2A2A2A",
        },
        // Only the print layout renders light; without this it would show MudBlazor's violet default
        PaletteLight = new PaletteLight
        {
            Primary = DefaultPrimary,
            PrimaryContrastText = "#FFFFFF",
            // app.css reads the lighten tone for accent text; on white that must be the bronze itself
            PrimaryLighten = DefaultPrimary,
            Secondary = DefaultSecondary,
            Tertiary = DefaultTertiary,
            Info = "#7F6F52",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            AppbarHeight = "60px",
        },
    };
}
