using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Decorations;

/// <summary>Pure rules behind decorations and certificates.</summary>
public static class DecorationRules
{
    /// <summary>What still fits on the certificate sheet next to name, symbol and signature.</summary>
    public const int MaxCitationLength = 600;

    /// <summary>Above this the certificate sets the citation smaller.</summary>
    public const int LongCitation = 350;
    public const int MaxReasonLength = 1000;
    public const int MaxPlaceLength = 120;
    public const int RecentDays = 30;
    public const int RecentMax = 5;
    public const int TileMax = 5;
    public const string DefaultPlace = "Los Santos, Observatorium 5020";

    /// <summary>Lowest rank that may sign certificates for the office.</summary>
    public const Rank SignatoryFloor = Rank.DeputyDirector;

    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>One award row as the plaque folding reads it.</summary>
    public sealed record PlaqueRow(
        string DecorationId, string Name, string Symbol, DecorationTier Tier, int Order, DateTime AwardedOn, bool Revoked);

    /// <summary>Which certificate a rank-history row earns; null for a demotion or an unchanged rank.</summary>
    public static CertificateKind? RankCertificateKind(Rank? previous, Rank conferred)
        => previous is null ? CertificateKind.Appointment
            : conferred > previous ? CertificateKind.Promotion
            : null;

    /// <summary>Leadership or the recipient may print, never a revoked award.</summary>
    public static bool MayPrint(ClaimsPrincipal viewer, string recipientId, bool revoked)
        => !revoked && viewer.IsInternalAgent()
            && (viewer.IsLeadership() || string.Equals(viewer.GetAgentId(), recipientId, StringComparison.Ordinal));

    /// <summary>Who may put the real name on a certificate: whoever may see real names, and the recipient.</summary>
    public static bool MayChooseRealName(ClaimsPrincipal viewer, string recipientId)
        => viewer.MayRealNameSee() || string.Equals(viewer.GetAgentId(), recipientId, StringComparison.Ordinal);

    /// <summary>An agent who can still sign: current staff from Deputy Director up.</summary>
    public static bool MaySign(Agent agent) => AgentSelection.IsSelectable(agent) && agent.Rank >= SignatoryFloor;

    /// <summary>The ceremony day; null when it lies after today.</summary>
    public static DateTime? CeremonyDay(DateTime? chosen, DateTime todayLocal)
    {
        var day = (chosen ?? todayLocal).Date;
        return day <= todayLocal.Date ? day : null;
    }

    /// <summary>Trimmed citation with unified line breaks.</summary>
    public static string NormalizeText(string? text)
        => (text ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Trim();

    /// <summary>Folds active awards into one plaque per decoration, best grade first.</summary>
    public static IReadOnlyList<DecorationPlaqueView> Plaques(IEnumerable<PlaqueRow> rows)
        => rows.Where(r => !r.Revoked)
            .GroupBy(r => r.DecorationId, StringComparer.Ordinal)
            .Select(g =>
            {
                var first = g.First();
                return (Order: first.Order, View: new DecorationPlaqueView(
                    g.Key, first.Name, first.Symbol, first.Tier, g.Count(), g.Max(r => r.AwardedOn)));
            })
            .OrderBy(p => p.View.Tier)
            .ThenBy(p => p.Order)
            .ThenBy(p => p.View.Name, StringComparer.CurrentCulture)
            .Select(p => p.View)
            .ToList();

    /// <summary>Long German date, e.g. "14. Oktober 2026".</summary>
    public static string LongDate(DateTime day) => day.ToString("d. MMMM yyyy", German);

    /// <summary>Board title of an award.</summary>
    public static string AnnouncementTitle(string decorationName, string codename)
        => Clip($"Ehrung: {decorationName} für {codename}", 200);

    /// <summary>Board body of an award; every value encoded, line breaks kept.</summary>
    public static string AnnouncementHtml(string decorationName, string codename, DateTime awardedOn,
        string citation, string? awardedByName)
    {
        var sb = new StringBuilder();
        sb.Append("<p><strong>").Append(Encode(codename)).Append("</strong> erhält das Ehrenzeichen <strong>")
            .Append(Encode(decorationName)).Append("</strong>.</p>");
        if (!string.IsNullOrWhiteSpace(citation))
        {
            sb.Append("<blockquote>")
                .Append(string.Join("<br>", NormalizeText(citation).Split('\n').Select(Encode)))
                .Append("</blockquote>");
        }
        sb.Append("<p>Verliehen am ").Append(LongDate(awardedOn));
        if (!string.IsNullOrWhiteSpace(awardedByName))
        {
            sb.Append(" durch ").Append(Encode(awardedByName));
        }
        sb.Append(".</p>");
        return sb.ToString();
    }

    /// <summary>Bell title for the recipient.</summary>
    public static string BellTitle(string decorationName) => Clip($"Dir wurde das Ehrenzeichen „{decorationName}“ verliehen.", 300);

    private static string Encode(string value) => WebUtility.HtmlEncode(value);

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
