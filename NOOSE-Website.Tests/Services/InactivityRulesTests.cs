using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>How the inactivity radar counts silence, excuses absences and picks the tier.</summary>
public sealed class InactivityRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);

    private static (DateOnly, DateOnly) Span(int fromDay, int toDay)
        => (new DateOnly(2026, 10, fromDay), new DateOnly(2026, 10, toDay));

    [Fact]
    public void Silence_counts_the_days_after_the_trace_up_to_today()
    {
        var (silent, absent) = InactivityRules.Count(new DateOnly(2026, 9, 30), Today, []);

        Assert.Equal(14, silent);
        Assert.Equal(0, absent);
    }

    [Fact]
    public void A_trace_today_or_in_the_future_is_no_silence()
    {
        Assert.Equal((0, 0), InactivityRules.Count(Today, Today, [Span(1, 31)]));
        Assert.Equal((0, 0), InactivityRules.Count(Today.AddDays(1), Today, []));
    }

    [Fact]
    public void Absence_days_inside_the_silence_are_excused_and_overlaps_count_once()
    {
        // silence: 2..14 October; absences 3..6 and 5..8 overlap into 3..8 = six days
        var (silent, absent) = InactivityRules.Count(new DateOnly(2026, 10, 1), Today, [Span(3, 6), Span(5, 8)]);

        Assert.Equal(13, silent);
        Assert.Equal(6, absent);
    }

    [Fact]
    public void Only_the_part_of_an_absence_inside_the_silence_counts()
    {
        // trace on the 5th: days 1..5 are not silence, days after today are not either
        var (silent, absent) = InactivityRules.Count(new DateOnly(2026, 10, 5), Today,
            [Span(1, 7), (new DateOnly(2026, 10, 13), new DateOnly(2026, 11, 2))]);

        Assert.Equal(9, silent);
        // 6th and 7th, then 13th and 14th
        Assert.Equal(4, absent);
    }

    [Fact]
    public void The_trace_day_itself_is_never_excused()
    {
        var (_, absent) = InactivityRules.Count(new DateOnly(2026, 10, 10), Today, [Span(10, 10)]);

        Assert.Equal(0, absent);
    }

    [Theory]
    [InlineData(13, InactivityTier.None)]
    [InlineData(14, InactivityTier.FollowUp)]
    [InlineData(29, InactivityTier.FollowUp)]
    [InlineData(30, InactivityTier.Note)]
    [InlineData(59, InactivityTier.Note)]
    [InlineData(60, InactivityTier.Termination)]
    [InlineData(400, InactivityTier.Termination)]
    public void Tiers_start_at_their_threshold(int days, InactivityTier expected)
        => Assert.Equal(expected, InactivityRules.Tier(days, InactivityThresholds.Default));

    [Fact]
    public void The_latest_trace_wins_and_the_release_is_the_floor()
    {
        var released = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        Assert.Equal((released, InactivityTraceKind.Released),
            InactivityRules.LastTrace(released, null, released.AddDays(-3), null, null));
        Assert.Equal((released.AddDays(4), InactivityTraceKind.Read),
            InactivityRules.LastTrace(released, null, released.AddDays(2), released.AddDays(4), released.AddDays(3)));
        Assert.Equal((released.AddDays(9), InactivityTraceKind.Seen),
            InactivityRules.LastTrace(released, null, released.AddDays(2), null, released.AddDays(9)));
    }

    [Fact]
    public void A_reactivation_is_a_floor_of_its_own()
    {
        var released = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);
        var unblocked = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

        // the old traces stem from before the block and no longer count
        Assert.Equal((unblocked, InactivityTraceKind.Reactivated),
            InactivityRules.LastTrace(released, unblocked, released.AddDays(30), null, released.AddDays(60)));
        Assert.Equal((unblocked.AddDays(1), InactivityTraceKind.Write),
            InactivityRules.LastTrace(released, unblocked, unblocked.AddDays(1), null, null));
    }

    [Fact]
    public void A_nudge_may_follow_only_after_the_cooldown()
    {
        var now = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(InactivityRules.MayNudge(null, now));
        Assert.False(InactivityRules.MayNudge(now.AddHours(-23), now));
        Assert.True(InactivityRules.MayNudge(now.AddHours(-24), now));
    }

    [Fact]
    public void A_running_absence_reports_its_latest_end()
    {
        Assert.Equal(new DateOnly(2026, 10, 20),
            InactivityRules.AbsentUntil(Today, [Span(10, 16), Span(12, 20), Span(1, 5)]));
        Assert.Null(InactivityRules.AbsentUntil(Today, [Span(1, 13), Span(15, 20)]));
    }

    [Fact]
    public void A_nudge_counts_only_until_the_next_trace()
    {
        var trace = new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.True(InactivityRules.NudgeIsCurrent(trace.AddDays(14), trace));
        Assert.False(InactivityRules.NudgeIsCurrent(trace.AddDays(-1), trace));
        Assert.False(InactivityRules.NudgeIsCurrent(null, trace));
    }

    [Theory]
    [InlineData(14, 30, 60)]
    [InlineData(1, 2, 3)]
    [InlineData(100, 200, 365)]
    public void Rising_thresholds_within_a_year_are_valid(int a, int b, int c)
        => InactivityRules.Validate(new InactivityThresholds(a, b, c));

    [Theory]
    [InlineData(0, 30, 60)]
    [InlineData(30, 30, 60)]
    [InlineData(30, 20, 60)]
    [InlineData(14, 60, 60)]
    [InlineData(14, 30, 366)]
    public void Flat_falling_or_out_of_range_thresholds_are_refused(int a, int b, int c)
        => Assert.Throws<InvalidOperationException>(() => InactivityRules.Validate(new InactivityThresholds(a, b, c)));

    [Fact]
    public void The_nudge_names_the_day_and_fits_the_bell()
    {
        var title = InactivityRules.NudgeTitle(new DateOnly(2026, 9, 30));

        Assert.Contains("30.09.2026", title);
        // Notification.Title is a 300-character column
        Assert.True(title.Length <= 300);
    }
}
