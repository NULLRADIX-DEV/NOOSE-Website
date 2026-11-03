using System.Globalization;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>Short address of a wanted notice (<c>FIB-FA-2026-0042</c> becomes <c>/g/2026-0042</c>) and how a typed one is read back.</summary>
public static partial class WantedShortLink
{
    /// <summary>Route prefix of the short address; it only redirects, so it is no public prefix.</summary>
    public const string Prefix = "/g";

    private const string QrLabel = "QR-Code zum Steckbrief";

    /// <summary>The short code of a case number, e.g. <c>2026-0042</c>; null for any other shape.</summary>
    public static string? CodeFor(string? caseNumber)
    {
        if (caseNumber is null)
        {
            return null;
        }
        var match = CaseNumberShape().Match(caseNumber);
        return match.Success ? $"{match.Groups["year"].Value}-{match.Groups["number"].Value}" : null;
    }

    /// <summary>The case number a typed short code stands for; null when it cannot be one.</summary>
    public static string? CaseNumberFor(string? code)
    {
        if (code is null)
        {
            return null;
        }
        var match = CodeShape().Match(code.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)
            || number < 1)
        {
            return null;
        }
        // four-digit counter
        return string.Create(CultureInfo.InvariantCulture,
            $"{CaseNumberService.AgencyPrefix}-{PublicWantedService.CaseNumberPrefix}-{match.Groups["year"].Value}-{number:0000}");
    }

    /// <summary>Site-relative path of the public profile.</summary>
    public static string ProfilePath(string caseNumber) => $"/gesucht/{Uri.EscapeDataString(caseNumber)}";

    /// <summary>The configured https site without trailing slash, never the request host; the default site otherwise.</summary>
    public static string SiteBase(string? configured)
    {
        if (Uri.TryCreate(configured?.Trim(), UriKind.Absolute, out var uri)
            && uri.Scheme == Uri.UriSchemeHttps
            && string.IsNullOrEmpty(uri.UserInfo))
        {
            return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        }
        return DiscordWebhookConfig.DefaultBaseUrl;
    }

    /// <summary>Address, short address and QR code of a live notice for its poster.</summary>
    public static WantedPosterLink Poster(string? siteBaseUrl, string caseNumber, bool tipsOn)
    {
        var site = SiteBase(siteBaseUrl);
        var profileUrl = site + ProfilePath(caseNumber);
        var code = CodeFor(caseNumber);
        var shortUrl = code is null ? profileUrl : $"{site}{Prefix}/{code}";

        // no redirect hop
        var modules = QrCode.Encode(profileUrl);
        var svg = modules is null ? null : QrCode.Svg(modules, QrLabel);

        return tipsOn
            ? new WantedPosterLink(WithoutScheme(shortUrl), WithoutScheme(profileUrl), svg, "Hinweis geben",
                "QR-Code scannen oder Adresse eingeben, dann auf dem Steckbrief „Hinweis geben“ wählen.")
            : new WantedPosterLink(WithoutScheme(shortUrl), WithoutScheme(profileUrl), svg, "Steckbrief online",
                "QR-Code scannen oder Adresse eingeben.");
    }

    private static string WithoutScheme(string url)
    {
        var at = url.IndexOf("://", StringComparison.Ordinal);
        return at < 0 ? url : url[(at + 3)..];
    }

    // ASCII digits only
    [GeneratedRegex("^(?:" + CaseNumberService.AgencyPrefix + "|NOOSE)-" + PublicWantedService.CaseNumberPrefix + @"-(?<year>[0-9]{4})-(?<number>[0-9]{1,9})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CaseNumberShape();

    // legacy NOOSE- prefix still typed from old posters
    [GeneratedRegex("^(?:(?:" + CaseNumberService.AgencyPrefix + "-|NOOSE-)?" + PublicWantedService.CaseNumberPrefix + @"-)?(?<year>[0-9]{4})-(?<number>[0-9]{1,9})\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CodeShape();
}
