using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Unit tests for the agenda/minutes read gate: rank early access plus the 2h-after-meeting window for any internal agent.</summary>
public class MeetingVisibilityTests
{
    // Fixed reference instant so every offset-based case is deterministic.
    private static readonly DateTime Now = new(2026, 7, 21, 12, 0, 0, DateTimeKind.Utc);

    // ---- PublicFrom ----

    [Fact]
    public void PublicFrom_without_end_is_start_plus_two_hours()
    {
        var start = new DateTime(2026, 7, 21, 9, 0, 0, DateTimeKind.Utc);

        Assert.Equal(start.AddHours(2), MeetingVisibility.PublicFrom(start, null));
    }

    [Fact]
    public void PublicFrom_with_end_is_end_plus_two_hours()
    {
        var start = new DateTime(2026, 7, 21, 9, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 7, 21, 11, 30, 0, DateTimeKind.Utc);

        Assert.Equal(end.AddHours(2), MeetingVisibility.PublicFrom(start, end));
    }

    // ---- 2h window: internal agent of any rank ----

    [Theory]
    [InlineData(-7201, true)]  // start 2h+1s ago -> public since 1s ago
    [InlineData(-7200, true)]  // start exactly 2h ago -> public exactly now (>=)
    [InlineData(-7199, false)] // start just under 2h ago -> not yet
    [InlineData(0, false)]     // meeting is now
    [InlineData(3600, false)]  // meeting is in the future
    public void Junior_sees_agenda_only_once_two_hours_past_start(int startOffsetSeconds, bool expected)
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee);
        var start = Now.AddSeconds(startOffsetSeconds);

        Assert.Equal(expected, MeetingVisibility.MayReadAgenda(user, start, null, Now));
    }

    [Fact]
    public void SpecialAgent_below_gate_still_gains_access_after_two_hours()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent);

        Assert.True(MeetingVisibility.MayReadAgenda(user, Now.AddHours(-3), null, Now));
    }

    [Fact]
    public void SpecialAgent_below_gate_is_blocked_before_the_window()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgent);

        Assert.False(MeetingVisibility.MayReadAgenda(user, Now.AddHours(1), null, Now));
    }

    // ---- rank/supervision early access (before the window) ----

    [Fact]
    public void SeniorSpecialAgent_sees_agenda_before_the_meeting()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SeniorSpecialAgent);

        Assert.True(MeetingVisibility.MayReadAgenda(user, Now.AddHours(5), null, Now));
    }

    [Fact]
    public void Admin_sees_agenda_before_the_meeting()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsAdmin();

        Assert.True(MeetingVisibility.MayReadAgenda(user, Now.AddHours(5), null, Now));
    }

    [Fact]
    public void Leadership_sees_agenda_before_the_meeting()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.SpecialAgentInCharge);

        Assert.True(MeetingVisibility.MayReadAgenda(user, Now.AddHours(5), null, Now));
    }

    [Fact]
    public void OnlyReader_supervision_sees_agenda_before_the_meeting()
    {
        // TeamLead without admin = read-only supervision, which reads everything.
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsTeamLead();

        Assert.True(MeetingVisibility.MayReadAgenda(user, Now.AddHours(5), null, Now));
    }

    // ---- partners are never admitted ----

    [Fact]
    public void Partner_never_sees_agenda_after_the_window()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief);

        Assert.False(MeetingVisibility.MayReadAgenda(user, Now.AddHours(-5), null, Now));
    }

    [Fact]
    public void Partner_never_sees_agenda_before_the_window()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().AsPartner(PartnerAgency.DoJ, PartnerRank.Chief);

        Assert.False(MeetingVisibility.MayReadAgenda(user, Now.AddHours(1), null, Now));
    }

    // ---- End anchors the window when present ----

    [Fact]
    public void With_end_set_the_window_is_measured_from_end_not_start()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee);
        // started 5h ago (start+2h long passed) but ended only 1h ago (end+2h not yet reached)
        var start = Now.AddHours(-5);
        var end = Now.AddHours(-1);

        Assert.False(MeetingVisibility.MayReadAgenda(user, start, end, Now));
    }

    [Fact]
    public void With_end_set_access_opens_two_hours_after_end()
    {
        ClaimsPrincipal user = ClaimsPrincipalBuilder.Agent().WithRank(Rank.Trainee);
        var start = Now.AddHours(-5);
        var end = Now.AddHours(-3); // end+2h passed 1h ago

        Assert.True(MeetingVisibility.MayReadAgenda(user, start, end, Now));
    }

    // ---- ViewerScope overload mirrors the principal overload ----

    [Fact]
    public void Scope_with_agenda_read_sees_agenda_before_the_meeting()
    {
        var scope = new ViewerScope(false, false, "lead", null, MayAgenda: true);

        Assert.True(MeetingVisibility.MayReadAgenda(scope, Now.AddHours(5), null, Now));
    }

    [Fact]
    public void Scope_internal_without_agenda_read_opens_after_two_hours()
    {
        var scope = new ViewerScope(false, false, "low", null, MayAgenda: false);

        Assert.True(MeetingVisibility.MayReadAgenda(scope, Now.AddHours(-3), null, Now));
    }

    [Fact]
    public void Scope_partner_is_never_admitted()
    {
        var scope = new ViewerScope(false, false, "p", PartnerAgency.DoJ, MayAgenda: false);

        Assert.False(MeetingVisibility.MayReadAgenda(scope, Now.AddHours(-3), null, Now));
    }
}
