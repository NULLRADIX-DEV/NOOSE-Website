using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Buckets, postponing and escalation of follow-ups, counted in Berlin calendar days.</summary>
public static class FollowupPlanRules
{
    public const string Route = "/wiedervorlagen";

    public const string BacklogRoute = "/wiedervorlagen/rueckstand";

    /// <summary>Days overdue after which leadership is told, once per due date.</summary>
    public const int EscalateAfterDays = 7;

    /// <summary>The one-click postpone steps the plan offers: a day and a week.</summary>
    public static readonly IReadOnlyList<int> PostponeDays = [1, 7];

    /// <summary>Rows either list loads at most, oldest due first.</summary>
    public const int MaxRows = 500;

    /// <summary>Overdue before today, today, the rest of this Monday-to-Sunday week, later.</summary>
    public static FollowupBucket Bucket(DateOnly due, DateOnly today)
    {
        if (due < today)
        {
            return FollowupBucket.Overdue;
        }
        if (due == today)
        {
            return FollowupBucket.Today;
        }
        return due <= EndOfWeek(today) ? FollowupBucket.ThisWeek : FollowupBucket.Later;
    }

    /// <summary>Sunday of the week the day falls in.</summary>
    public static DateOnly EndOfWeek(DateOnly day)
        => day.AddDays(6 - ((int)day.DayOfWeek + 6) % 7);

    /// <summary>The new due instant; an overdue one counts from today, so "+1 Tag" never stays in the past.</summary>
    public static DateTime Postponed(DateTime dueUtc, int days, DateTime nowUtc)
    {
        if (!PostponeDays.Contains(days))
        {
            throw new InvalidOperationException("Eine Wiedervorlage lässt sich nur um einen Tag oder eine Woche verschieben.");
        }
        var local = MeetingTime.Local(dueUtc);
        var today = MeetingTime.Day(nowUtc);
        var from = DateOnly.FromDateTime(local) < today ? today : DateOnly.FromDateTime(local);
        // keep time of day
        return MeetingTime.ToUtc(from.AddDays(days).ToDateTime(TimeOnly.FromTimeSpan(local.TimeOfDay)));
    }

    /// <summary>Calendar days since the due day; zero on the day, negative while still ahead.</summary>
    public static int DaysOverdue(DateTime dueUtc, DateTime nowUtc)
        => MeetingTime.Day(nowUtc).DayNumber - MeetingTime.Day(dueUtc).DayNumber;

    /// <summary>Latest due instant that is long enough overdue to escalate.</summary>
    public static DateTime EscalationCutoff(DateTime nowUtc) => nowUtc.AddDays(-EscalateAfterDays);

    /// <summary>Bell title of the escalation; names no record and no agent, the backlog page does that per viewer.</summary>
    public static string EscalationTitle
        => $"Wiedervorlagen sind seit mindestens {EscalateAfterDays} Tagen überfällig. Der Rückstand zeigt, welche.";
}
