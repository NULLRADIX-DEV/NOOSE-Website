using System.Globalization;
using System.Text;

namespace NOOSE_Website.Services;

/// <summary>One VEVENT. Timed events carry UTC instants; all-day events carry dates with an exclusive end.</summary>
public sealed record IcsEvent(string Uid, DateTime StampUtc, string Summary, DateTime Start, DateTime? End,
    bool AllDay, string? Url = null);

/// <summary>Hand-written RFC 5545 serializer: CRLF lines, folding at 75 octets, escaped text, UTC timestamps.</summary>
public static class IcsWriter
{
    public const string ContentType = "text/calendar; charset=utf-8";

    private const int MaxOctets = 75;
    private const string Crlf = "\r\n";

    /// <summary>Full VCALENDAR document for the events.</summary>
    public static string Serialize(string productId, string calendarName, IEnumerable<IcsEvent> events)
    {
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:" + productId);
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "METHOD:PUBLISH");
        Line(sb, "X-WR-CALNAME:" + EscapeText(calendarName));
        Line(sb, "REFRESH-INTERVAL;VALUE=DURATION:PT1H");
        Line(sb, "X-PUBLISHED-TTL:PT1H");

        foreach (var e in events)
        {
            Line(sb, "BEGIN:VEVENT");
            Line(sb, "UID:" + EscapeText(e.Uid));
            Line(sb, "DTSTAMP:" + Utc(e.StampUtc));
            if (e.AllDay)
            {
                var start = e.Start.Date;
                var end = e.End is { } until && until.Date > start ? until.Date : start.AddDays(1);
                Line(sb, "DTSTART;VALUE=DATE:" + Date(start));
                Line(sb, "DTEND;VALUE=DATE:" + Date(end));
            }
            else
            {
                Line(sb, "DTSTART:" + Utc(e.Start));
                if (e.End is { } end && end > e.Start)
                {
                    Line(sb, "DTEND:" + Utc(end));
                }
            }
            Line(sb, "SUMMARY:" + EscapeText(e.Summary));
            if (IsPlainUrl(e.Url))
            {
                Line(sb, "URL:" + e.Url);
            }
            Line(sb, "END:VEVENT");
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    /// <summary>TEXT escaping: backslash, semicolon, comma and any line break; other control characters are dropped.</summary>
    public static string EscapeText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }
        var sb = new StringBuilder(text.Length + 8);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case ';': sb.Append("\\;"); break;
                case ',': sb.Append("\\,"); break;
                case '\r':
                    if (i + 1 < text.Length && text[i + 1] == '\n')
                    {
                        i++;
                    }
                    sb.Append("\\n");
                    break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append(c); break;
                default:
                    if (!char.IsControl(c))
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Appends one content line, folded so no physical line passes 75 octets and no code point is split.</summary>
    public static void Line(StringBuilder sb, string line)
    {
        var used = 0;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            if (used + size > MaxOctets)
            {
                // continuation lines start with one space that counts toward the limit
                sb.Append(Crlf).Append(' ');
                used = 1;
            }
            sb.Append(rune.ToString());
            used += size;
        }
        sb.Append(Crlf);
    }

    public static string Utc(DateTime utc)
        => utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    public static string Date(DateTime day)
        => day.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static bool IsPlainUrl(string? url)
        => url is { Length: > 0 }
           && (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
           && !url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));
}
