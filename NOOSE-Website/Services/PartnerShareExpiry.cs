using System.Globalization;

namespace NOOSE_Website.Services;

/// <summary>Quick end-date choices offered when a record is released to a partner.</summary>
public enum PartnerShareDuration
{
    Unlimited = 0,
    OneDay = 1,
    SevenDays = 2,
    ThirtyDays = 3,
    Custom = 4,
}

/// <summary>Rules for the optional end date of a partner release; one place for validity, presets and wording.</summary>
public static class PartnerShareExpiry
{
    /// <summary>True while a release with this end date still grants access.</summary>
    public static bool IsActive(DateTime? validUntil, DateTime utcNow) => validUntil is null || validUntil.Value > utcNow;

    /// <summary>Rejects an end date that is not in the future; null (unlimited) always passes.</summary>
    public static DateTime? Require(DateTime? validUntil, DateTime utcNow)
    {
        if (validUntil is null)
        {
            return null;
        }
        var utc = DateTime.SpecifyKind(validUntil.Value, DateTimeKind.Utc);
        if (utc <= utcNow)
        {
            throw new InvalidOperationException("Das Enddatum der Freigabe muss in der Zukunft liegen.");
        }
        return utc;
    }

    /// <summary>End of the chosen calendar day in the pinned zone, as UTC.</summary>
    public static DateTime EndOfDay(DateOnly day) => MeetingTime.ToUtc(day.ToDateTime(new TimeOnly(23, 59)));

    /// <summary>End instant of a preset; null for unlimited, and for custom (the caller picks the date).</summary>
    public static DateTime? FromPreset(PartnerShareDuration preset, DateTime utcNow) => preset switch
    {
        PartnerShareDuration.OneDay => utcNow.AddDays(1),
        PartnerShareDuration.SevenDays => utcNow.AddDays(7),
        PartnerShareDuration.ThirtyDays => utcNow.AddDays(30),
        _ => null,
    };

    /// <summary>Stable wording for the audit trail: "unbegrenzt" or "bis 12.11.2026 23:59"; no countdown, so it never differs between reads.</summary>
    public static string Until(DateTime? validUntil)
        => validUntil is null ? "unbegrenzt" : $"bis {MeetingTime.Text(DateTime.SpecifyKind(validUntil.Value, DateTimeKind.Utc))}";

    /// <summary>Short German wording: "unbegrenzt", "bis 12.11.2026 23:59 (noch 6 Tage)" or "abgelaufen".</summary>
    public static string Describe(DateTime? validUntil, DateTime utcNow)
    {
        if (validUntil is null)
        {
            return "unbegrenzt";
        }
        var until = DateTime.SpecifyKind(validUntil.Value, DateTimeKind.Utc);
        return until <= utcNow
            ? "abgelaufen"
            : $"bis {MeetingTime.Text(until)} ({Remaining(until, utcNow)})";
    }

    /// <summary>Time left as "noch 6 Tage", "noch 5 Std." or "noch 12 Min."; empty once over.</summary>
    public static string Remaining(DateTime validUntil, DateTime utcNow)
    {
        var left = DateTime.SpecifyKind(validUntil, DateTimeKind.Utc) - utcNow;
        if (left <= TimeSpan.Zero)
        {
            return string.Empty;
        }
        var de = CultureInfo.GetCultureInfo("de-DE");
        if (left.TotalDays >= 2)
        {
            return $"noch {((int)left.TotalDays).ToString(de)} Tage";
        }
        if (left.TotalHours >= 1)
        {
            return $"noch {((int)left.TotalHours).ToString(de)} Std.";
        }
        return $"noch {Math.Max(1, (int)left.TotalMinutes).ToString(de)} Min.";
    }
}
