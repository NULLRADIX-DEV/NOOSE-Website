using System.Security.Claims;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Who may trigger, read and end an emergency alarm, when it counts as running, and the shared limits.</summary>
public class EmergencyAlarmRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 18, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Agent(string id, Rank rank)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(rank).Build();

    private static ClaimsPrincipal Leader(string id = "lead") => Agent(id, Rank.SectionChief);

    private static ClaimsPrincipal Junior(string id = "junior") => Agent(id, Rank.Trainee);

    private static ClaimsPrincipal Partner(string id = "partner1")
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ClaimsPrincipal Supervision(string id = "tl")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).AsTeamLead().Build();

    private static EmergencyAlarm Alarm(string triggeredById = "junior", DateTime? expiry = null, DateTime? endedAt = null)
        => new()
        {
            TriggeredById = triggeredById,
            TriggeredAt = Now,
            Expiry = expiry ?? Now.Add(EmergencyAlarmRules.Duration),
            EndedAt = endedAt,
        };

    [Fact]
    public void Limits_AreFixed()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), EmergencyAlarmRules.Duration);
        Assert.Equal(500, EmergencyAlarmRules.MaxSituation);
        Assert.Equal(128, EmergencyAlarmRules.MaxLocation);
    }

    [Fact]
    public void MayTrigger_InternalWriters_NotPartnerNotSupervision()
    {
        Assert.True(EmergencyAlarmRules.MayTrigger(Leader()));
        Assert.True(EmergencyAlarmRules.MayTrigger(Junior()));
        Assert.False(EmergencyAlarmRules.MayTrigger(Partner()));
        Assert.False(EmergencyAlarmRules.MayTrigger(Supervision()));
    }

    [Fact]
    public void NeedsReason_OnlyBelowLeadership()
    {
        Assert.False(EmergencyAlarmRules.NeedsReason(Agent("sc", Rank.SectionChief)));
        Assert.False(EmergencyAlarmRules.NeedsReason(ClaimsPrincipalBuilder.Agent("adm").WithRank(Rank.SpecialAgent).AsAdmin().Build()));
        Assert.True(EmergencyAlarmRules.NeedsReason(Agent("sac", Rank.SpecialAgentInCharge)));
        Assert.True(EmergencyAlarmRules.NeedsReason(Junior()));
    }

    [Fact]
    public void IsOpen_FalseWhenEndedOrExpired()
    {
        Assert.False(EmergencyAlarmRules.IsOpen(Alarm(endedAt: Now.AddMinutes(-1)), Now));
        Assert.False(EmergencyAlarmRules.IsOpen(Alarm(expiry: Now), Now));
        Assert.False(EmergencyAlarmRules.IsOpen(Alarm(expiry: Now.AddSeconds(-1)), Now));
        Assert.True(EmergencyAlarmRules.IsOpen(Alarm(expiry: Now.AddSeconds(1)), Now));
    }

    [Fact]
    public void MayEnd_TriggererOrLeadership()
    {
        var alarm = Alarm(triggeredById: "junior");

        Assert.True(EmergencyAlarmRules.MayEnd(Junior("junior"), alarm));
        Assert.False(EmergencyAlarmRules.MayEnd(Junior("other"), alarm));
        Assert.True(EmergencyAlarmRules.MayEnd(Leader(), alarm));
        Assert.False(EmergencyAlarmRules.MayEnd(Supervision(), alarm));
        Assert.False(EmergencyAlarmRules.MayEnd(Partner(), alarm));
    }

    [Fact]
    public void MayEnd_TriggererWhoLostWriteAccess_IsRefused()
    {
        var alarm = Alarm(triggeredById: "tl");

        Assert.False(EmergencyAlarmRules.MayEnd(Supervision("tl"), alarm));
    }

    [Fact]
    public void MayRead_InternalAndSupervision_NotPartner()
    {
        Assert.True(EmergencyAlarmRules.MayRead(Junior()));
        Assert.True(EmergencyAlarmRules.MayRead(Leader()));
        Assert.True(EmergencyAlarmRules.MayRead(Supervision()));
        Assert.False(EmergencyAlarmRules.MayRead(Partner()));
        Assert.False(EmergencyAlarmRules.MayRead(ClaimsPrincipalBuilder.Anonymous()));
    }
}
