using System.Globalization;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>German day input as typed in Discord: heute, morgen, übermorgen, +3, 20.11., 20.11.26, 20.11.2026, optionally with a time.</summary>
public static partial class DiscordDateParser
{
    [GeneratedRegex(@"^(?<d>\d{1,2})\.(?<m>\d{1,2})\.?(?<y>\d{2}|\d{4})?$")]
    private static partial Regex DayPattern();

    [GeneratedRegex(@"^(?<h>\d{1,2})(:(?<min>\d{2}))?( ?uhr)?$", RegexOptions.IgnoreCase)]
    private static partial Regex TimePattern();

    /// <summary>The Berlin day the text names, relative to <paramref name="today"/>; null when it is not a day.</summary>
    public static DateOnly? ParseDay(string? text, DateOnly today)
    {
        var value = text?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        switch (value)
        {
            case "heute":
                return today;
            case "morgen":
                return today.AddDays(1);
            case "übermorgen" or "uebermorgen":
                return today.AddDays(2);
            case "gestern":
                return today.AddDays(-1);
        }
        if (value.StartsWith('+') && int.TryParse(value[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var plus) && plus <= 366)
        {
            return today.AddDays(plus);
        }
        var match = DayPattern().Match(value);
        if (!match.Success)
        {
            return null;
        }
        var day = int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups["m"].Value, CultureInfo.InvariantCulture);
        int year;
        if (match.Groups["y"].Success)
        {
            year = int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (year < 100)
            {
                year += 2000;
            }
        }
        else
        {
            year = today.Year;
        }
        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return null;
        }
        var result = new DateOnly(year, month, day);
        // a bare 03.01. in December means next January
        if (!match.Groups["y"].Success && result < today.AddDays(-60))
        {
            result = result.AddYears(1);
        }
        return result;
    }

    /// <summary>Day plus optional time ("morgen 18:30", "20.11. 9 Uhr"); without a time <paramref name="defaultTime"/>. Returns UTC.</summary>
    public static DateTime? ParseMoment(string? text, DateOnly today, TimeOnly defaultTime)
    {
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }
        var parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var day = ParseDay(parts[0], today);
        if (day is null)
        {
            return null;
        }
        var time = defaultTime;
        if (parts.Length == 2)
        {
            var match = TimePattern().Match(parts[1]);
            if (!match.Success)
            {
                return null;
            }
            var hour = int.Parse(match.Groups["h"].Value, CultureInfo.InvariantCulture);
            var minute = match.Groups["min"].Success ? int.Parse(match.Groups["min"].Value, CultureInfo.InvariantCulture) : 0;
            if (hour > 23 || minute > 59)
            {
                return null;
            }
            time = new TimeOnly(hour, minute);
        }
        return BerlinTime.ToUtc(day.Value, time);
    }
}
