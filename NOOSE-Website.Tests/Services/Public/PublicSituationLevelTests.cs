using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The published situation level: a closed ladder, read back through an allowlist.</summary>
/// <remarks>
/// The stored value comes out of a hand-editable key/value row and is rendered on an [AllowAnonymous] page, so the
/// parser has to answer "unknown" rather than throw — the same failure class as an unparsable query value.
/// </remarks>
public class PublicSituationLevelTests
{
    private static readonly PublicSituationLevel[] Values =
        Enum.GetValues<PublicSituationLevel>();

    // the dark page background and the card surface the level colours are drawn on
    private static readonly string[] Backgrounds = ["#1C1C1C", "#242424"];

    // CIE76 distance; ~2.3 is a just-noticeable difference, 30 is "different colour" at a glance
    private const double MinDistance = 30;

    // WCAG AA for normal text
    private const double MinContrast = 4.5;

    [Fact]
    public void AllListsEveryLevel()
        => Assert.Equal(Values.Order().ToArray(), PublicSituationLevelDisplay.All.Order().ToArray());

    [Fact]
    public void AllRunsFromDefconFiveDownToOne()
    {
        var numbers = PublicSituationLevelDisplay.All
            .Select(l => Regex.Match(PublicSituationLevelDisplay.Name(l), @"^DEFCON ([1-5])$"))
            .Select(m => m.Success ? int.Parse(m.Groups[1].Value) : -1)
            .ToArray();

        Assert.Equal(new[] { 5, 4, 3, 2, 1 }, numbers);
    }

    [Fact]
    public void TheFifthLevelSitsBetweenHochAndKritisch()
    {
        var all = PublicSituationLevelDisplay.All.ToList();

        Assert.Equal(5, all.Count);
        Assert.Equal(all.IndexOf(PublicSituationLevel.Hoch) + 1, all.IndexOf(PublicSituationLevel.Schwer));
        Assert.Equal(all.IndexOf(PublicSituationLevel.Schwer) + 1, all.IndexOf(PublicSituationLevel.Kritisch));
        // the picker order and the severity order are one and the same
        Assert.Equal(all.OrderBy(l => (int)l).ToList(), all);
    }

    [Fact]
    public void EveryLevelHasALabelAndAHint()
    {
        var offenders = Values
            .Where(l => PublicSituationLevelDisplay.Name(l) is "—" or ""
                || PublicSituationLevelDisplay.Hint(l).Length == 0)
            .Select(l => l.ToString())
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0,
            "Jede Stufe hat Beschriftung und Kurztext: " + string.Join(", ", offenders));
    }

    [Fact]
    public void NoTwoLevelsShareALabelOrAHint()
    {
        var names = Values.Select(PublicSituationLevelDisplay.Name).ToArray();
        var hints = Values.Select(PublicSituationLevelDisplay.Hint).ToArray();

        Assert.Equal(names.Length, names.Distinct().Count());
        Assert.Equal(hints.Length, hints.Distinct().Count());
    }

    [Fact]
    public void EveryLevelHasAnExplicitHexColour()
    {
        var offenders = Values
            .Where(l => !Regex.IsMatch(PublicSituationLevelDisplay.Hex(l), "^#[0-9A-F]{6}$"))
            .Select(l => l.ToString())
            .Order()
            .ToArray();

        Assert.True(offenders.Length == 0, "Stufenfarbe ist kein Hex-Wert: " + string.Join(", ", offenders));
    }

    [Fact]
    public void EveryLevelColourIsReadableOnTheDarkSurfaces()
    {
        // a level nobody can read is worse than no level at all
        var offenders = Values
            .SelectMany(l => Backgrounds
                .Select(bg => (Level: l, Background: bg, Ratio: Contrast(PublicSituationLevelDisplay.Hex(l), bg))))
            .Where(x => x.Ratio < MinContrast)
            .Select(x => $"{x.Level} auf {x.Background}: {x.Ratio:0.00}")
            .ToArray();

        Assert.True(offenders.Length == 0, "Zu wenig Kontrast: " + string.Join("; ", offenders));
        // filled chips put the page background on the colour, so the measured ratio holds for their text too
        Assert.Contains(PublicSituationLevelDisplay.OnHex, Backgrounds);
    }

    [Fact]
    public void NoTwoLevelsLookAlike()
    {
        // pinned on the hex values, not on enum members: with a warm theme the palette colours collapse together
        var offenders = new List<string>();
        for (var i = 0; i < Values.Length; i++)
        {
            for (var j = i + 1; j < Values.Length; j++)
            {
                var distance = Distance(PublicSituationLevelDisplay.Hex(Values[i]),
                    PublicSituationLevelDisplay.Hex(Values[j]));
                if (distance < MinDistance)
                {
                    offenders.Add($"{Values[i]}/{Values[j]}: {distance:0.0}");
                }
            }
        }

        Assert.True(offenders.Count == 0, "Stufenfarben zu ähnlich: " + string.Join("; ", offenders));
    }

    [Fact]
    public void TheStylesCarryTheLevelColour()
    {
        foreach (var level in Values)
        {
            var hex = PublicSituationLevelDisplay.Hex(level);

            Assert.Contains($"color:{hex}", PublicSituationLevelDisplay.TextStyle(level));
            Assert.Contains($"background-color:{hex}", PublicSituationLevelDisplay.FilledStyle(level));
            Assert.Contains($"border-color:{hex}", PublicSituationLevelDisplay.OutlineStyle(level));
        }
    }

    [Fact]
    public void ParseRoundTripsEveryStoredKey()
    {
        foreach (var level in Values)
        {
            Assert.Equal(level, PublicSituationLevelDisplay.Parse(PublicSituationLevelDisplay.Key(level)));
        }
    }

    [Theory]
    [InlineData(PublicSituationLevel.Niedrig, "Niedrig")]
    [InlineData(PublicSituationLevel.Erhoeht, "Erhoeht")]
    [InlineData(PublicSituationLevel.Hoch, "Hoch")]
    [InlineData(PublicSituationLevel.Schwer, "Schwer")]
    [InlineData(PublicSituationLevel.Kritisch, "Kritisch")]
    public void TheStoredKeysArePinned(PublicSituationLevel level, string key)
        // These strings sit in the SystemSettings rows of every installation. A round-trip test cannot protect
        // them: rename one and Parse(Key(x)) still succeeds while every stored row stops parsing, /lage goes silent
        // and nothing turns red. Changing one of these is a data migration, not a rename. The first four predate
        // the fifth level, so they must read exactly as before.
        => Assert.Equal(key, PublicSituationLevelDisplay.Key(level));

    [Fact]
    public void TheStoredKeysAreAsciiOnly()
        => Assert.All(Values, l => Assert.Matches("^[A-Za-z]+$", PublicSituationLevelDisplay.Key(l)));

    [Fact]
    public void TheGermanLabelIsNotTheStoredKey()
    {
        // The separation only means something if the label cannot be stored by accident, the way
        // WarnhinweisColourChoice keeps "Error" apart from "Rot". With the DEFCON labels every level differs from
        // its key. Deliberately asserted as a relationship and never as the label text: pinning the wording here
        // would re-create exactly the coupling the split removes, and the label is UI copy that is meant to stay
        // editable.
        foreach (var level in Values)
        {
            var label = PublicSituationLevelDisplay.Name(level);
            var key = PublicSituationLevelDisplay.Key(level);

            Assert.NotEqual(label, key);
            Assert.Null(PublicSituationLevelDisplay.Parse(label));
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("7")]
    [InlineData("3")]
    [InlineData("Kritisch ")]
    [InlineData("kritisch")]
    [InlineData("Schwer ")]
    [InlineData("schwer")]
    [InlineData("Erhoht")]
    [InlineData("Erhöht")]
    [InlineData("Panisch")]
    [InlineData("DEFCON 2")]
    public void AnythingElseIsUnknown(string? stored)
        => Assert.Null(PublicSituationLevelDisplay.Parse(stored));

    private static double[] Rgb(string hex)
        => [Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16)];

    private static double Linear(double channel)
    {
        var c = channel / 255d;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    private static double Luminance(string hex)
    {
        var rgb = Rgb(hex);
        return 0.2126 * Linear(rgb[0]) + 0.7152 * Linear(rgb[1]) + 0.0722 * Linear(rgb[2]);
    }

    private static double Contrast(string a, string b)
    {
        var (hi, lo) = (Math.Max(Luminance(a), Luminance(b)), Math.Min(Luminance(a), Luminance(b)));
        return (hi + 0.05) / (lo + 0.05);
    }

    private static double[] Lab(string hex)
    {
        var rgb = Rgb(hex).Select(Linear).ToArray();
        var x = (0.4124564 * rgb[0] + 0.3575761 * rgb[1] + 0.1804375 * rgb[2]) / 0.95047;
        var y = 0.2126729 * rgb[0] + 0.7151522 * rgb[1] + 0.0721750 * rgb[2];
        var z = (0.0193339 * rgb[0] + 0.1191920 * rgb[1] + 0.9503041 * rgb[2]) / 1.08883;

        static double F(double t) => t > 216d / 24389d ? Math.Cbrt(t) : (24389d / 27d * t + 16d) / 116d;

        return [116 * F(y) - 16, 500 * (F(x) - F(y)), 200 * (F(y) - F(z))];
    }

    private static double Distance(string a, string b)
    {
        var (la, lb) = (Lab(a), Lab(b));
        return Math.Sqrt(la.Zip(lb, (p, q) => (p - q) * (p - q)).Sum());
    }
}
