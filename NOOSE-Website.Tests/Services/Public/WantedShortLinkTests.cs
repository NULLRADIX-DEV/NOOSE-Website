using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The short address on the poster: how a case number shrinks to it and how a typed one is read back.</summary>
public partial class WantedShortLinkTests
{
    [Theory]
    [InlineData("FIB-FA-2026-0042", "2026-0042")]
    [InlineData("FIB-FA-2026-12345", "2026-12345")]
    [InlineData("fib-fa-2026-0042", "2026-0042")]
    // legacy prefix
    [InlineData("NOOSE-FA-2026-0042", "2026-0042")]
    public void ACaseNumber_ShrinksToYearAndCounter(string caseNumber, string code)
        => Assert.Equal(code, WantedShortLink.CodeFor(caseNumber));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    // other counters
    [InlineData("FIB-P-2026-0001")]
    [InlineData("FIB-F-2026-0001")]
    [InlineData("FIB-FA-26-0042")]
    [InlineData("FIB-FA-2026-0042-x")]
    [InlineData("FIB-FA-2026-0042\n")]
    public void AnythingElse_HasNoShortCode(string? caseNumber)
        => Assert.Null(WantedShortLink.CodeFor(caseNumber));

    [Theory]
    [InlineData("2026-0042")]
    // typing variants
    [InlineData("2026-42")]
    [InlineData("2026-00042")]
    [InlineData("FA-2026-0042")]
    [InlineData("fa-2026-0042")]
    [InlineData("FIB-FA-2026-0042")]
    [InlineData("NOOSE-FA-2026-0042")]
    [InlineData(" 2026-0042 ")]
    public void ATypedCode_ReadsBackToTheCaseNumber(string code)
        => Assert.Equal("FIB-FA-2026-0042", WantedShortLink.CaseNumberFor(code));

    [Fact]
    public void ACounterAbove9999_KeepsAllItsDigits()
        => Assert.Equal("FIB-FA-2026-12345", WantedShortLink.CaseNumberFor("2026-12345"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("26-42")]
    [InlineData("2026-0")]
    [InlineData("2026-")]
    [InlineData("2026--42")]
    [InlineData("P-2026-0001")]
    [InlineData("FIB-P-2026-0001")]
    [InlineData("2026-1234567890")]
    // non-ASCII digits
    [InlineData("٢٠٢٦-٠٠٤٢")]
    [InlineData("2026-0042/druck")]
    public void AnythingElse_ReadsBackToNothing(string? code)
        => Assert.Null(WantedShortLink.CaseNumberFor(code));

    [Theory]
    [InlineData("FIB-FA-2026-0001")]
    [InlineData("FIB-FA-2026-0042")]
    [InlineData("FIB-FA-2027-9999")]
    [InlineData("FIB-FA-2026-10000")]
    public void EveryMintedCaseNumber_SurvivesTheRoundTrip(string caseNumber)
        => Assert.Equal(caseNumber, WantedShortLink.CaseNumberFor(WantedShortLink.CodeFor(caseNumber)));

    [Theory]
    [InlineData("https://fibhq.com", "https://fibhq.com")]
    [InlineData("https://fibhq.com/", "https://fibhq.com")]
    [InlineData(" https://demo.fibhq.com/ ", "https://demo.fibhq.com")]
    [InlineData("https://Example.org/noose/", "https://example.org/noose")]
    // query dropped
    [InlineData("https://example.org/?x=1#y", "https://example.org")]
    public void TheConfiguredSite_IsUsedWithoutTrailingSlash(string configured, string expected)
        => Assert.Equal(expected, WantedShortLink.SiteBase(configured));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("fibhq.com")]
    [InlineData("/gesucht")]
    // https only
    [InlineData("http://fibhq.com")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:pass@fibhq.com")]
    public void AnUnusableSite_FallsBackToTheDefault(string? configured)
        => Assert.Equal("https://fibhq.com", WantedShortLink.SiteBase(configured));

    [Fact]
    public void ThePoster_PrintsTheConfiguredSiteAndEncodesTheProfile()
    {
        var link = WantedShortLink.Poster("https://demo.example.org/", "FIB-FA-2026-0042", tipsOn: false);

        Assert.Equal("demo.example.org/g/2026-0042", link.ShortText);
        Assert.Equal("demo.example.org/gesucht/FIB-FA-2026-0042", link.ProfileText);
        Assert.NotNull(link.QrSvg);
        Assert.StartsWith("<svg", link.QrSvg, StringComparison.Ordinal);
        Assert.DoesNotContain("fibhq.com", link.ShortText + link.ProfileText, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePosterCode_IsTheFullProfileAddress()
    {
        // full address
        var link = WantedShortLink.Poster(null, "FIB-FA-2026-0042", tipsOn: true);
        var expected = NOOSE_Website.Services.QrCode.Svg(
            NOOSE_Website.Services.QrCode.Encode("https://fibhq.com/gesucht/FIB-FA-2026-0042")!, "QR-Code zum Steckbrief");

        Assert.Equal(expected, link.QrSvg);
    }

    [Fact]
    public void AnOddCaseNumber_FallsBackToTheFullAddress()
    {
        var link = WantedShortLink.Poster(null, "FIB-FA-ALT 7", tipsOn: false);

        Assert.Equal("fibhq.com/gesucht/FIB-FA-ALT%207", link.ShortText);
        Assert.Equal(link.ProfileText, link.ShortText);
    }

    [Fact]
    public void TheTip_IsNamedOnlyWhileTheTipsModuleIsOn()
    {
        var on = WantedShortLink.Poster(null, "FIB-FA-2026-0042", tipsOn: true);
        var off = WantedShortLink.Poster(null, "FIB-FA-2026-0042", tipsOn: false);

        Assert.Contains("Hinweis", on.Heading + on.Caption, StringComparison.Ordinal);
        Assert.DoesNotContain("Hinweis", off.Heading + off.Caption, StringComparison.Ordinal);
        Assert.Equal(on.ShortText, off.ShortText);
        Assert.Equal(on.QrSvg, off.QrSvg);
    }

    [Fact]
    public void TheShortRoute_StaysOutOfThePublicPrefixes()
    {
        // redirect only
        Assert.False(PublicRoutes.IsPublic(WantedShortLink.Prefix));
        Assert.False(PublicRoutes.IsPublic(WantedShortLink.Prefix + "/2026-0042"));
    }

    [Fact]
    public void NoPage_ClaimsTheShortRoute()
    {
        var root = Components();
        var taken = Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories)
            .SelectMany(f => PageDirective().Matches(File.ReadAllText(f)).Select(m => m.Groups[1].Value))
            .Where(route => route.Equals(WantedShortLink.Prefix, StringComparison.OrdinalIgnoreCase)
                || route.StartsWith(WantedShortLink.Prefix + "/", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        Assert.Empty(taken);
    }

    [Fact]
    public void ThePoster_NamesNoHostOfItsOwn()
    {
        // configured site only
        var poster = File.ReadAllText(Path.Combine(Components(), "Pages", "Public", "WantedPoster.razor"));

        Assert.DoesNotContain("fibhq.com", poster, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("GetPosterLinkAsync", poster, StringComparison.Ordinal);
    }

    private static string Components([CallerFilePath] string here = "")
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(here)!, "..", "..", "..", "NOOSE-Website", "Components"));
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");
        return root;
    }

    [GeneratedRegex("""@page\s+"([^"]+)"\s*""")]
    private static partial Regex PageDirective();
}
