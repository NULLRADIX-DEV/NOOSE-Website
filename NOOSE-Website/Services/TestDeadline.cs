using NOOSE_Website.Models.Abstractions;

namespace NOOSE_Website.Services;

/// <summary>The one place a test attempt's deadline is computed and judged.</summary>
/// <remarks>
/// Written out at each call site the read, draft, submit and sweep paths would drift, and a countdown that
/// disagrees with the sweep reads as a bug rather than a decision. Everything here is pure: the caller passes
/// its own hoisted "now", so one operation cannot straddle two instants.
/// There is only ever one clock — the server's. The browser's is never read, sent or compared.
/// </remarks>
public static class TestDeadline
{
    /// <summary>Highest configurable processing time; a typo must not create a deadline nobody can reach.</summary>
    public const int MaxMinutes = 600;

    /// <summary>A submission arriving this late still counts; past it only the stored draft is finalised.</summary>
    public static readonly TimeSpan SubmitGrace = TimeSpan.FromMinutes(2);

    /// <summary>Inside this lead the mandatory-question check is dropped.</summary>
    /// <remarks>A submit here is indistinguishable from the automatic one, and refusing it for a blank
    /// mandatory answer would throw away everything the timeout would have kept. It grants no extra time:
    /// it only permits what the timeout permits moments later anyway.</remarks>
    public static readonly TimeSpan AutoSubmitLead = TimeSpan.FromSeconds(10);

    /// <summary>Deadline of a fresh attempt; no minutes means this attempt carries no limit.</summary>
    public static DateTime? For(DateTime startedAt, int? minutes)
        => minutes is > 0 ? startedAt.AddMinutes(minutes.Value) : null;

    /// <summary>Past the deadline, without tolerance: this is what the marker and the flag are derived from.</summary>
    public static bool IsExpired(DateTime? deadlineAt, DateTime now) => deadlineAt is { } due && due <= now;

    /// <summary>Past the deadline plus the grace window; a payload arriving now is ignored.</summary>
    public static bool GraceOver(DateTime? deadlineAt, DateTime now)
        => deadlineAt is { } due && due + SubmitGrace <= now;

    /// <summary>Inside the closing seconds or beyond them.</summary>
    public static bool IsClosing(DateTime? deadlineAt, DateTime now)
        => deadlineAt is { } due && due - AutoSubmitLead <= now;

    /// <summary>Time left, never negative; null when the attempt carries no limit.</summary>
    public static TimeSpan? Remaining(DateTime? deadlineAt, DateTime now)
        => deadlineAt is { } due ? (due > now ? due - now : TimeSpan.Zero) : null;

    /// <summary>Minutes a started attempt was granted in total, extension included.</summary>
    /// <remarks>TimeLimitMinutes is the base frozen at the start and an extension never rewrites it, so the
    /// two columns add up exactly once.</remarks>
    public static int? AllowedMinutes(ITestAttempt assignment)
        => assignment.TimeLimitMinutes is { } minutes ? minutes + assignment.ExtraMinutes : null;

    /// <summary>Same, but before the start, where the base still comes from the test.</summary>
    public static int? AllowedMinutes(ITestAttempt assignment, int? testMinutes)
        => (assignment.TimeLimitMinutes ?? Clamp(testMinutes)) is { } baseMinutes
            ? baseMinutes + assignment.ExtraMinutes
            : null;

    /// <summary>Minutes actually used, from the start to the hand-in; null while either end is missing.</summary>
    public static int? UsedMinutes(DateTime? startedAt, DateTime? completedAt)
        => startedAt is { } start && completedAt is { } done
            ? (int)Math.Max(0, Math.Round((done - start).TotalMinutes))
            : null;

    /// <summary>A running attempt blocks structural edits to its test; deliberately bounded by the deadline.</summary>
    /// <remarks>An attempt without a limit is never "live" here. Otherwise a started but never submitted
    /// attempt would block the test's questions forever, which is worse than today.</remarks>
    public static bool IsLive(ITestAttempt assignment, DateTime now)
        => assignment is { StartedAt: not null, CompletedAt: null, DeadlineAt: not null }
            && assignment.DeadlineAt.Value > now;

    /// <summary>Clamp a configured value; anything at or below zero means "no limit".</summary>
    public static int? Clamp(int? minutes) => minutes is > 0 ? Math.Clamp(minutes.Value, 1, MaxMinutes) : null;
}
