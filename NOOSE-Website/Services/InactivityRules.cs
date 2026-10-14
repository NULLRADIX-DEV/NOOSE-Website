using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>How the inactivity radar counts silence; the one place the day arithmetic lives.</summary>
/// <remarks>
/// Days are Berlin calendar days, the same unit absences are filed in. An absence day inside the silence is
/// excused, so a fortnight's sign-off does not push anyone up a tier; overlapping absences count once.
/// </remarks>
public static class InactivityRules
{
    /// <summary>A page visit is stamped at most this often; the radar counts whole days, finer adds only writes.</summary>
    public static readonly TimeSpan SeenGranularity = TimeSpan.FromHours(1);

    /// <summary>Thresholds must rise strictly and stay within a year.</summary>
    public static void Validate(InactivityThresholds thresholds)
    {
        if (thresholds.FollowUpDays < 1 || thresholds.TerminationDays > InactivityThresholds.MaxDays)
        {
            throw new InvalidOperationException(
                $"Die Schwellen müssen zwischen 1 und {InactivityThresholds.MaxDays} Tagen liegen.");
        }
        if (thresholds.FollowUpDays >= thresholds.NoteDays || thresholds.NoteDays >= thresholds.TerminationDays)
        {
            throw new InvalidOperationException(
                "Die Schwellen müssen aufsteigen: Nachfassen vor Vermerk vor Kündigungsvorschlag.");
        }
    }

    /// <summary>One nudge per agent in this span; also what keeps two leaders clicking together from pinging twice.</summary>
    public static readonly TimeSpan NudgeCooldown = TimeSpan.FromHours(24);

    /// <summary>The latest trace, never earlier than the release or a reactivation; ties keep the first kind named.</summary>
    public static (DateTime At, InactivityTraceKind Kind) LastTrace(DateTime released, DateTime? reactivated,
        DateTime? write, DateTime? read, DateTime? seen)
    {
        var at = released;
        var kind = InactivityTraceKind.Released;
        Take(reactivated, InactivityTraceKind.Reactivated);
        Take(write, InactivityTraceKind.Write);
        Take(read, InactivityTraceKind.Read);
        Take(seen, InactivityTraceKind.Seen);
        return (at, kind);

        void Take(DateTime? value, InactivityTraceKind candidate)
        {
            if (value is { } v && v > at)
            {
                at = v;
                kind = candidate;
            }
        }
    }

    /// <summary>Days since the last trace's day, and how many of them an absence covers.</summary>
    public static (int Silent, int Absent) Count(DateOnly lastTraceDay, DateOnly today,
        IEnumerable<(DateOnly From, DateOnly To)> absences)
    {
        var silent = today.DayNumber - lastTraceDay.DayNumber;
        if (silent <= 0)
        {
            return (0, 0);
        }

        // only the silence itself: from the day after the trace up to today
        var first = lastTraceDay.DayNumber + 1;
        var last = today.DayNumber;
        var covered = new HashSet<int>();
        foreach (var (from, to) in absences)
        {
            for (var day = Math.Max(from.DayNumber, first); day <= Math.Min(to.DayNumber, last); day++)
            {
                covered.Add(day);
            }
        }
        return (silent, covered.Count);
    }

    public static InactivityTier Tier(int inactiveDays, InactivityThresholds thresholds)
        => inactiveDays >= thresholds.TerminationDays ? InactivityTier.Termination
            : inactiveDays >= thresholds.NoteDays ? InactivityTier.Note
            : inactiveDays >= thresholds.FollowUpDays ? InactivityTier.FollowUp
            : InactivityTier.None;

    /// <summary>The last day of an absence running today, the latest if several overlap.</summary>
    public static DateOnly? AbsentUntil(DateOnly today, IEnumerable<(DateOnly From, DateOnly To)> absences)
        => absences.Where(a => a.From <= today && a.To >= today)
            .Select(a => (DateOnly?)a.To)
            .Max();

    /// <summary>A nudge counts until the agent leaves a trace after it.</summary>
    public static bool NudgeIsCurrent(DateTime? nudgedAt, DateTime lastTraceAt)
        => nudgedAt is { } at && at > lastTraceAt;

    /// <summary>Whether the cooldown since the last nudge has passed.</summary>
    public static bool MayNudge(DateTime? nudgedAt, DateTime nowUtc)
        => nudgedAt is not { } at || at <= nowUtc - NudgeCooldown;

    /// <summary>Bell title of a nudge; names the day, not the count, which the agent cannot check.</summary>
    public static string NudgeTitle(DateOnly lastTraceDay)
        => $"Die Führung hat seit dem {lastTraceDay:dd.MM.yyyy} kein Lebenszeichen von dir gesehen. "
           + "Melde dich bitte oder trag eine Abmeldung ein.";

    /// <summary>Starting text of the personnel note the radar suggests.</summary>
    public static string NoteHtml(DateOnly lastTraceDay, int inactiveDays)
        => $"<p>Kein Lebenszeichen seit dem {lastTraceDay:dd.MM.yyyy}: {inactiveDays} Tage ohne Abmeldung.</p>";

    /// <summary>Starting reason of a termination the radar suggests.</summary>
    public static string TerminationReason(DateOnly lastTraceDay, int inactiveDays)
        => $"Inaktivität: kein Lebenszeichen seit dem {lastTraceDay:dd.MM.yyyy}, {inactiveDays} Tage ohne Abmeldung.";
}
