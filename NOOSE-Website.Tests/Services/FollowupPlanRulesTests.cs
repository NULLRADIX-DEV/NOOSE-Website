using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Buckets, postponing and day counting of the follow-up plan, on Berlin calendar days.</summary>
public sealed class FollowupPlanRulesTests
{
    // Wednesday, 4 November 2026
    private static readonly DateOnly Wednesday = new(2026, 11, 4);

    [Theory]
    [InlineData(2026, 11, 3, FollowupBucket.Overdue)]
    [InlineData(2026, 10, 1, FollowupBucket.Overdue)]
    [InlineData(2026, 11, 4, FollowupBucket.Today)]
    [InlineData(2026, 11, 5, FollowupBucket.ThisWeek)]
    [InlineData(2026, 11, 8, FollowupBucket.ThisWeek)]
    [InlineData(2026, 11, 9, FollowupBucket.Later)]
    [InlineData(2027, 1, 1, FollowupBucket.Later)]
    public void A_due_day_falls_into_one_bucket(int year, int month, int day, FollowupBucket expected)
        => Assert.Equal(expected, FollowupPlanRules.Bucket(new DateOnly(year, month, day), Wednesday));

    [Fact]
    public void On_a_sunday_this_week_is_empty_and_monday_is_later()
    {
        var sunday = new DateOnly(2026, 11, 8);

        Assert.Equal(FollowupBucket.Today, FollowupPlanRules.Bucket(sunday, sunday));
        Assert.Equal(FollowupBucket.Later, FollowupPlanRules.Bucket(sunday.AddDays(1), sunday));
    }

    [Theory]
    [InlineData(2026, 11, 2, 2026, 11, 8)]
    [InlineData(2026, 11, 4, 2026, 11, 8)]
    [InlineData(2026, 11, 8, 2026, 11, 8)]
    public void The_week_ends_on_sunday(int y, int m, int d, int ey, int em, int ed)
        => Assert.Equal(new DateOnly(ey, em, ed), FollowupPlanRules.EndOfWeek(new DateOnly(y, m, d)));

    [Fact]
    public void Postponing_a_future_followup_moves_it_from_its_own_day()
    {
        var now = Utc(2026, 11, 4, 10);
        var due = MeetingTime.ToUtc(new DateTime(2026, 11, 10, 0, 0, 0));

        var moved = FollowupPlanRules.Postponed(due, 1, now);

        Assert.Equal(new DateTime(2026, 11, 11, 0, 0, 0), MeetingTime.Local(moved));
    }

    [Fact]
    public void Postponing_an_overdue_followup_counts_from_today()
    {
        var now = Utc(2026, 11, 4, 10);
        var due = MeetingTime.ToUtc(new DateTime(2026, 10, 20, 0, 0, 0));

        Assert.Equal(new DateTime(2026, 11, 5, 0, 0, 0), MeetingTime.Local(FollowupPlanRules.Postponed(due, 1, now)));
        Assert.Equal(new DateTime(2026, 11, 11, 0, 0, 0), MeetingTime.Local(FollowupPlanRules.Postponed(due, 7, now)));
    }

    [Fact]
    public void Postponing_keeps_the_time_of_day_across_the_clock_change()
    {
        // the clocks go back on 25 October 2026; a week later is still 09:30 on the wall
        var now = Utc(2026, 10, 20, 10);
        var due = MeetingTime.ToUtc(new DateTime(2026, 10, 22, 9, 30, 0));

        var moved = FollowupPlanRules.Postponed(due, 7, now);

        Assert.Equal(new DateTime(2026, 10, 29, 9, 30, 0), MeetingTime.Local(moved));
        Assert.Equal(TimeSpan.FromHours(7 * 24 + 1), moved - due);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(30)]
    public void Only_a_day_or_a_week_is_offered(int days)
        => Assert.Throws<InvalidOperationException>(() => FollowupPlanRules.Postponed(Utc(2026, 11, 4, 0), days, Utc(2026, 11, 4, 0)));

    [Fact]
    public void Days_overdue_count_calendar_days_not_hours()
    {
        var now = Utc(2026, 11, 4, 0, 30); // 01:30 in Berlin
        var lateYesterday = MeetingTime.ToUtc(new DateTime(2026, 11, 3, 23, 0, 0));

        Assert.Equal(1, FollowupPlanRules.DaysOverdue(lateYesterday, now));
        Assert.Equal(0, FollowupPlanRules.DaysOverdue(MeetingTime.ToUtc(new DateTime(2026, 11, 4, 0, 0, 0)), now));
        Assert.Equal(-2, FollowupPlanRules.DaysOverdue(MeetingTime.ToUtc(new DateTime(2026, 11, 6, 0, 0, 0)), now));
    }

    [Fact]
    public void The_escalation_title_names_the_threshold_and_nothing_else()
    {
        Assert.Contains(FollowupPlanRules.EscalateAfterDays.ToString(), FollowupPlanRules.EscalationTitle);
        Assert.Equal(FollowupPlanRules.EscalationCutoff(Utc(2026, 11, 8, 0)), Utc(2026, 11, 1, 0));
    }

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);
}
