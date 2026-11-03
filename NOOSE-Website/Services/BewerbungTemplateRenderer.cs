using System.Net;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Renders recruiting message templates for the applicant: the recipient (BEWERBER) is addressed
/// by name, while the sender's name token (NAME) is always replaced by a redaction block so the NOOSE agent
/// stays anonymous.</summary>
public static partial class BewerbungTemplateRenderer
{
    /// <summary>Fixed-width redaction block shown instead of the sender's name.</summary>
    public const string Redaction = "███████";

    /// <summary>Fallback salutation when no applicant name is known.</summary>
    private const string ApplicantFallback = "Bewerber/in";

    /// <summary>The sender's rank as the applicant reads it: always the neutral title.</summary>
    /// <remarks>The division never leaves the house, so a letter carries no HRT title, whatever the sender's unit.</remarks>
    public static string? SenderRank(Rank? rank) => rank is { } r ? RankDisplay.Name(r) : null;

    /// <summary>Prepare a template body for insertion into an applicant letter: redact the sender NAME, address
    /// the recipient BEWERBER by name, fill DIENSTGRAD (the sender's rank); DATUM/UHRZEIT stay as tokens.</summary>
    public static string RenderForApplicant(string html, string? applicantName, string? dienstgrad)
    {
        var applicant = ApplicantReplacement(applicantName);
        html = NameToken().Replace(html, Redaction);
        html = ApplicantToken().Replace(html, _ => applicant);
        if (!string.IsNullOrWhiteSpace(dienstgrad))
        {
            html = RankToken().Replace(html, _ => WebUtility.HtmlEncode(dienstgrad));
        }
        return html;
    }

    /// <summary>Defense in depth before persisting an applicant-facing message: redact the sender NAME token and
    /// fill any leftover recipient BEWERBER token. HTML-safe — only visible text is rewritten, never tags.</summary>
    public static string Redact(string html, string? applicantName)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? string.Empty;
        }
        var applicant = ApplicantReplacement(applicantName);
        // rewrite only the text segments, never the inside of < ... > tags, so markup/attributes stay intact
        return TagOrText().Replace(html, m =>
        {
            if (m.Value.StartsWith('<'))
            {
                return m.Value;
            }
            var text = NameToken().Replace(m.Value, Redaction);
            text = ApplicantToken().Replace(text, _ => applicant);
            return text;
        });
    }

    /// <summary>HTML-encoded applicant name, or a neutral fallback when no name is available.</summary>
    private static string ApplicantReplacement(string? applicantName)
    {
        var name = applicantName?.Trim();
        return string.IsNullOrWhiteSpace(name) ? ApplicantFallback : WebUtility.HtmlEncode(name);
    }

    /// <summary>Replace the DATUM / UHRZEIT tokens the agent picks in the editor; empty values stay as tokens.</summary>
    public static string FillDateTime(string html, string? date, string? time)
    {
        if (string.IsNullOrEmpty(html))
        {
            return html ?? string.Empty;
        }
        if (!string.IsNullOrWhiteSpace(date))
        {
            html = DateToken().Replace(html, _ => WebUtility.HtmlEncode(date));
        }
        if (!string.IsNullOrWhiteSpace(time))
        {
            html = TimeToken().Replace(html, _ => WebUtility.HtmlEncode(time));
        }
        return html;
    }

    /// <summary>True if the body still carries an unfilled DATUM token.</summary>
    public static bool HasDateToken(string html) => !string.IsNullOrEmpty(html) && DateToken().IsMatch(html);

    /// <summary>True if the body still carries an unfilled UHRZEIT token.</summary>
    public static bool HasTimeToken(string html) => !string.IsNullOrEmpty(html) && TimeToken().IsMatch(html);

    /// <summary>Flatten template HTML to plain text (paragraph/break aware) for the plain-text conversation.</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }
        var text = BlockBreak().Replace(html, "\n");
        text = TagStrip().Replace(text, string.Empty);
        text = WebUtility.HtmlDecode(text);
        text = ExcessBlankLines().Replace(text, "\n\n");
        return text.Trim();
    }

    [GeneratedRegex(@"</p>|<br\s*/?>|</li>|</h[1-6]>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreak();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagStrip();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex ExcessBlankLines();

    [GeneratedRegex(@"\bNAME\b")]
    private static partial Regex NameToken();

    [GeneratedRegex(@"\bBEWERBER\b")]
    private static partial Regex ApplicantToken();

    [GeneratedRegex(@"\bDIENSTGRAD\b")]
    private static partial Regex RankToken();

    [GeneratedRegex(@"\bDATUM\b")]
    private static partial Regex DateToken();

    [GeneratedRegex(@"\bUHRZEIT\b")]
    private static partial Regex TimeToken();

    [GeneratedRegex(@"<[^>]+>|[^<]+")]
    private static partial Regex TagOrText();
}
