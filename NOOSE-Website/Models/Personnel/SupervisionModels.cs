using System.Globalization;
using NOOSE_Website.Models.CounterIntel;

namespace NOOSE_Website.Models.Personnel;

/// <summary>One figure of the sheet and the median of all selectable agents over the same window.</summary>
/// <remarks><see cref="Team"/> is null when nobody is selectable, so there is nothing to compare with.</remarks>
public sealed record SupervisionFigure(int Value, double? Team);

/// <summary>The latest trace, read as the inactivity radar reads it.</summary>
/// <param name="SilentDays">Calendar days since the trace's day.</param>
/// <param name="AbsentDays">Those of them an absence covers.</param>
public sealed record SupervisionActivity(DateTime LastTraceAt, InactivityTraceKind Kind, int SilentDays, int AbsentDays);

/// <summary>The access-log part of the sheet; only leadership without the read-only supervision gets it.</summary>
public sealed record SupervisionCounterIntel(
    SupervisionFigure Accesses,
    int DistinctRecords,
    int OffHours,
    IReadOnlyList<TypeCount> ByType,
    IReadOnlyList<InsiderFlag> Flags);

/// <summary>Everything the leadership weighs about one agent before a promotion, warning or termination talk.</summary>
/// <remarks>
/// Every count runs over the window that starts at <see cref="SinceUtc"/>, except <see cref="Badges"/>, which are
/// milestones and count whole. <see cref="CounterIntel"/> is null for a viewer who may not use the cockpit.
/// </remarks>
public sealed record SupervisionSheet(
    string AgentId,
    int Days,
    DateTime SinceUtc,
    SupervisionActivity Activity,
    SupervisionFigure Writes,
    SupervisionFigure FollowupsOnTime,
    SupervisionFigure FollowupsLate,
    SupervisionFigure FollowupsOverdue,
    SupervisionFigure MeetingsPresent,
    SupervisionFigure MeetingsSignedOff,
    SupervisionFigure MeetingsMissing,
    SupervisionFigure AbsenceDays,
    SupervisionFigure Points,
    SupervisionFigure Badges,
    SupervisionFigure Commendations,
    SupervisionFigure Disciplinary,
    SupervisionCounterIntel? CounterIntel);

/// <summary>The windows the sheet offers.</summary>
public static class SupervisionWindow
{
    public const int Default = 90;

    public static IReadOnlyList<int> Choices { get; } = [30, 90, 365];

    /// <summary>The asked window when it is one of the choices, else the default.</summary>
    public static int Normalize(int? days) => days is { } d && Choices.Contains(d) ? d : Default;
}

public static class SupervisionMath
{
    /// <summary>The middle value, or the mean of the two middle values; null for no values.</summary>
    public static double? Median(IEnumerable<int> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}

public static class SupervisionDisplay
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    /// <summary>One decimal at most, German separator.</summary>
    public static string Number(double value) => value.ToString("0.#", German);

    /// <summary>A window's total as a weekly rate.</summary>
    public static string PerWeek(double total, int days) => Number(days <= 0 ? 0 : total * 7 / days);

    /// <summary>The median beside a figure; empty when there is none.</summary>
    public static string Team(double? median) => median is { } m ? $"Team {Number(m)}" : "";

    /// <summary>The median beside a weekly rate, scaled the same way.</summary>
    public static string TeamPerWeek(double? median, int days)
        => median is { } m ? $"Team {PerWeek(m, days)}" : "";

    public static string Window(int days) => days == 365 ? "1 Jahr" : $"{days} Tage";
}
