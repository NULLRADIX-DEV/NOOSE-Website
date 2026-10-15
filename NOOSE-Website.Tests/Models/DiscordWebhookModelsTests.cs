using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Models;

public class DiscordWebhookModelsTests
{
    private const string HrbRoleId = "1515098218545938442";
    private const string NooseRoleId = "1479854499853238475";

    // Routable set per RoutableTypes list in the source.
    private static readonly NotificationType[] RoutableTypesList =
    {
        NotificationType.Announcement,
        NotificationType.Followup,
        NotificationType.SituationReport,
        NotificationType.Recruiting,
        NotificationType.Mention,
        NotificationType.JobAssigned,
        NotificationType.JobDueSoon,
        NotificationType.MeetingScheduled,
        NotificationType.MeetingReminder,
        NotificationType.AppointmentScheduled,
        NotificationType.PersonnelEntry,
        NotificationType.AgentTerminated,
        NotificationType.DecorationAwarded,
        NotificationType.PublicWantedPublished,
        NotificationType.PublicWantedBountyRaised,
        NotificationType.PublicTicketCreated,
        NotificationType.PublicPressPublished,
        NotificationType.PublicCaptureReported,
        NotificationType.PublicTipCreated,
        NotificationType.PingDigest,
        NotificationType.AcknowledgmentReminder,
        NotificationType.SavedSearchHit,
        NotificationType.InactivityNudge,
        NotificationType.FactionThreatBoard,
        NotificationType.TestAssigned,
        NotificationType.TestDueReminder,
    };

    // Enum members NOT in the routable list.
    private static readonly NotificationType[] NonRoutableTypesList =
    {
        NotificationType.RequestDecided,
        NotificationType.Account,
        NotificationType.RecordModified,
        NotificationType.AppointmentAssigned,
        NotificationType.AbsenceFiled,
        // the desk notice fires on a citizen reply too, so routing it would ping on every line of a running tip
        NotificationType.PublicTipReceived,
        NotificationType.PublicTipAnswered,
        // an internal operating fact: NotifyManyAsync pushes every routable category, so a routable one here would
        // post each expiry into the public channel
        NotificationType.PublicWantedExpired,
        // who stands out is for the leadership bell alone; a shared channel would show it was rung
        NotificationType.CounterIntelFinding,
        NotificationType.NooseiAnomaly,
        // the hand-in is for the grader's bell, the result for the agent's: neither belongs in a channel
        NotificationType.TestSubmitted,
        NotificationType.TestGraded,
        // a closed group or an invite list must not show in a shared channel
        NotificationType.PresentationLive,
    };

    // ----- IsRoutable: true branch -----

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.Followup)]
    [InlineData(NotificationType.SituationReport)]
    [InlineData(NotificationType.Recruiting)]
    [InlineData(NotificationType.Mention)]
    [InlineData(NotificationType.JobAssigned)]
    [InlineData(NotificationType.JobDueSoon)]
    [InlineData(NotificationType.MeetingScheduled)]
    [InlineData(NotificationType.MeetingReminder)]
    [InlineData(NotificationType.AppointmentScheduled)]
    [InlineData(NotificationType.PublicWantedPublished)]
    [InlineData(NotificationType.PublicWantedBountyRaised)]
    [InlineData(NotificationType.PublicTicketCreated)]
    public void IsRoutable_forRoutableTypes_returnsTrue(NotificationType type)
    {
        Assert.True(DiscordRouting.IsRoutable(type));
    }

    // ----- IsRoutable: false branch -----

    [Theory]
    [InlineData(NotificationType.RequestDecided)]
    [InlineData(NotificationType.Account)]
    [InlineData(NotificationType.RecordModified)]
    [InlineData(NotificationType.AppointmentAssigned)]
    [InlineData(NotificationType.AbsenceFiled)]
    public void IsRoutable_forNonRoutableTypes_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.IsRoutable(type));
    }

    [Fact]
    public void IsRoutable_forUndefinedEnumValue_returnsFalse()
    {
        Assert.False(DiscordRouting.IsRoutable((NotificationType)999));
    }

    // ----- PingsRecipients: true branch -----

    [Theory]
    [InlineData(NotificationType.Mention)]
    [InlineData(NotificationType.Followup)]
    [InlineData(NotificationType.JobAssigned)]
    [InlineData(NotificationType.JobDueSoon)]
    [InlineData(NotificationType.PingDigest)]
    [InlineData(NotificationType.AcknowledgmentReminder)]
    [InlineData(NotificationType.SavedSearchHit)]
    [InlineData(NotificationType.DecorationAwarded)]
    [InlineData(NotificationType.InactivityNudge)]
    [InlineData(NotificationType.TestAssigned)]
    [InlineData(NotificationType.TestDueReminder)]
    public void PingsRecipients_forRecipientCategories_returnsTrue(NotificationType type)
    {
        Assert.True(DiscordRouting.PingsRecipients(type));
    }

    // ----- PingsRecipients: false branch (routable non-recipients) -----

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.SituationReport)]
    [InlineData(NotificationType.Recruiting)]
    [InlineData(NotificationType.MeetingScheduled)]
    [InlineData(NotificationType.MeetingReminder)]
    [InlineData(NotificationType.AppointmentScheduled)]
    [InlineData(NotificationType.PublicWantedPublished)]
    [InlineData(NotificationType.PublicTicketCreated)]
    public void PingsRecipients_forRoutableRoleCategories_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.PingsRecipients(type));
    }

    // ----- PingsRecipients: false branch (non-routable) -----

    [Theory]
    [InlineData(NotificationType.RequestDecided)]
    [InlineData(NotificationType.Account)]
    [InlineData(NotificationType.RecordModified)]
    [InlineData(NotificationType.AppointmentAssigned)]
    [InlineData(NotificationType.AbsenceFiled)]
    public void PingsRecipients_forNonRoutableTypes_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.PingsRecipients(type));
    }

    [Fact]
    public void PingsRecipients_forUndefinedEnumValue_returnsFalse()
    {
        Assert.False(DiscordRouting.PingsRecipients((NotificationType)999));
    }

    // ----- PingsRole: true branch (routable AND not a recipient) -----

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.SituationReport)]
    [InlineData(NotificationType.Recruiting)]
    [InlineData(NotificationType.MeetingScheduled)]
    [InlineData(NotificationType.MeetingReminder)]
    [InlineData(NotificationType.AppointmentScheduled)]
    [InlineData(NotificationType.PublicWantedPublished)]
    [InlineData(NotificationType.PublicTicketCreated)]
    public void PingsRole_forRoutableRoleCategories_returnsTrue(NotificationType type)
    {
        Assert.True(DiscordRouting.PingsRole(type));
    }

    // ----- PingsRole: false branch (routable but recipient) -----

    [Theory]
    [InlineData(NotificationType.Mention)]
    [InlineData(NotificationType.Followup)]
    [InlineData(NotificationType.JobAssigned)]
    [InlineData(NotificationType.JobDueSoon)]
    public void PingsRole_forRecipientCategories_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.PingsRole(type));
    }

    // ----- PingsRole: false branch (not routable) -----

    [Theory]
    [InlineData(NotificationType.RequestDecided)]
    [InlineData(NotificationType.Account)]
    [InlineData(NotificationType.RecordModified)]
    [InlineData(NotificationType.AppointmentAssigned)]
    [InlineData(NotificationType.AbsenceFiled)]
    public void PingsRole_forNonRoutableTypes_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.PingsRole(type));
    }

    [Fact]
    public void PingsRole_forUndefinedEnumValue_returnsFalse()
    {
        Assert.False(DiscordRouting.PingsRole((NotificationType)999));
    }

    // ----- PingsNobody: the announcement-only bucket -----

    [Fact]
    public void PingsNobody_forTermination_returnsTrue()
    {
        Assert.True(DiscordRouting.PingsNobody(NotificationType.AgentTerminated));
    }

    [Fact]
    public void ThreatBoard_isRoutableButPingsNobodyAndOffersNoRoleField()
    {
        Assert.True(DiscordRouting.IsRoutable(NotificationType.FactionThreatBoard));
        Assert.True(DiscordRouting.PingsNobody(NotificationType.FactionThreatBoard));
        Assert.False(DiscordRouting.PingsRole(NotificationType.FactionThreatBoard));
        Assert.False(DiscordRouting.PingsRecipients(NotificationType.FactionThreatBoard));
        Assert.DoesNotContain(NotificationType.FactionThreatBoard, DiscordRouting.RoleRoutableTypes);
    }

    [Fact]
    public void DiscordWebhookConfig_threatBoardThreshold_defaultsToNinety()
    {
        var config = new DiscordWebhookConfig(true, "https://example.test",
            new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(), IncludeHeadline: true);

        Assert.Equal(90, config.ThreatBoardThreshold);
        Assert.Equal(90, new DiscordWebhookConfigInput().ThreatBoardThreshold);
    }

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.Mention)]
    [InlineData(NotificationType.PersonnelEntry)]
    [InlineData(NotificationType.AppointmentScheduled)]
    [InlineData(NotificationType.AbsenceFiled)]
    public void PingsNobody_forEveryOtherCategory_returnsFalse(NotificationType type)
    {
        Assert.False(DiscordRouting.PingsNobody(type));
    }

    [Fact]
    public void PingsNobody_forUndefinedEnumValue_returnsFalse()
    {
        Assert.False(DiscordRouting.PingsNobody((NotificationType)999));
    }

    [Fact]
    public void PingsRole_forTermination_returnsFalse()
    {
        // the no-ping bucket must not fall through into the role branch, or the panel would offer a role field
        Assert.False(DiscordRouting.PingsRole(NotificationType.AgentTerminated));
        Assert.DoesNotContain(NotificationType.AgentTerminated, DiscordRouting.RoleRoutableTypes);
    }

    // ----- PingsRole / PingsRecipients partition invariant -----

    [Fact]
    public void PingsRole_andPingsRecipients_areMutuallyExclusive()
    {
        foreach (NotificationType type in Enum.GetValues<NotificationType>())
        {
            Assert.False(
                DiscordRouting.PingsRole(type) && DiscordRouting.PingsRecipients(type),
                $"{type} must not both ping role and recipients");
        }
    }

    [Fact]
    public void RoutableTypes_partitionIntoRoleRecipientAndNobodyExactlyOnce()
    {
        foreach (NotificationType type in RoutableTypesList)
        {
            // Every routable type falls in exactly one ping bucket: role, recipients or nobody.
            var buckets = new[]
            {
                DiscordRouting.PingsRole(type),
                DiscordRouting.PingsRecipients(type),
                DiscordRouting.PingsNobody(type),
            };
            Assert.Equal(1, buckets.Count(hit => hit));
        }
    }

    // ----- DefaultRole: Recruiting arm vs. fallback arm -----

    [Fact]
    public void DefaultRole_forRecruiting_returnsHrbRole()
    {
        Assert.Equal(HrbRoleId, DiscordRouting.DefaultRole(NotificationType.Recruiting));
    }

    [Theory]
    [InlineData(NotificationType.Announcement)]
    [InlineData(NotificationType.Followup)]
    [InlineData(NotificationType.SituationReport)]
    [InlineData(NotificationType.Mention)]
    [InlineData(NotificationType.JobAssigned)]
    [InlineData(NotificationType.JobDueSoon)]
    [InlineData(NotificationType.MeetingScheduled)]
    [InlineData(NotificationType.MeetingReminder)]
    [InlineData(NotificationType.RequestDecided)]
    [InlineData(NotificationType.Account)]
    [InlineData(NotificationType.RecordModified)]
    [InlineData(NotificationType.AppointmentAssigned)]
    // the leadership role id is admin-configured; the default stays the generic fallback
    [InlineData(NotificationType.AppointmentScheduled)]
    [InlineData(NotificationType.PublicTicketCreated)]
    [InlineData(NotificationType.AbsenceFiled)]
    public void DefaultRole_forNonRecruiting_returnsNooseRole(NotificationType type)
    {
        Assert.Equal(NooseRoleId, DiscordRouting.DefaultRole(type));
    }

    [Fact]
    public void DefaultRole_forUndefinedEnumValue_returnsNooseRole()
    {
        Assert.Equal(NooseRoleId, DiscordRouting.DefaultRole((NotificationType)999));
    }

    // ----- RoutableTypes / RoleRoutableTypes contents -----

    [Fact]
    public void RoutableTypes_containsExactlyTheExpectedCategories()
    {
        Assert.Equal(RoutableTypesList.Length, DiscordRouting.RoutableTypes.Count);
        foreach (NotificationType type in RoutableTypesList)
        {
            Assert.Contains(type, DiscordRouting.RoutableTypes);
        }
        foreach (NotificationType type in NonRoutableTypesList)
        {
            Assert.DoesNotContain(type, DiscordRouting.RoutableTypes);
        }
    }

    [Fact]
    public void RoleRoutableTypes_isExactlyTheRoutableRoleCategories()
    {
        NotificationType[] expected =
        {
            NotificationType.Announcement,
            NotificationType.SituationReport,
            NotificationType.Recruiting,
            NotificationType.MeetingScheduled,
            NotificationType.MeetingReminder,
            NotificationType.AppointmentScheduled,
            NotificationType.PublicWantedPublished,
            NotificationType.PublicWantedBountyRaised,
            NotificationType.PublicTicketCreated,
            NotificationType.PublicPressPublished,
            NotificationType.PublicCaptureReported,
            NotificationType.PublicTipCreated,
        };

        Assert.Equal(expected.Length, DiscordRouting.RoleRoutableTypes.Count);
        foreach (NotificationType type in expected)
        {
            Assert.Contains(type, DiscordRouting.RoleRoutableTypes);
        }

        // Every entry pings a role and none ping recipients.
        foreach (NotificationType type in DiscordRouting.RoleRoutableTypes)
        {
            Assert.True(DiscordRouting.PingsRole(type));
            Assert.False(DiscordRouting.PingsRecipients(type));
        }
    }

    [Fact]
    public void CaptureReport_pingsTheAgencyRoleAndNotTheReporter()
    {
        Assert.True(DiscordRouting.IsRoutable(NotificationType.PublicCaptureReported));
        Assert.True(DiscordRouting.PingsRole(NotificationType.PublicCaptureReported));
        Assert.False(DiscordRouting.PingsRecipients(NotificationType.PublicCaptureReported));
        Assert.False(DiscordRouting.PingsNobody(NotificationType.PublicCaptureReported));
        // the fallback role, i.e. NOOSE - a capture concerns the whole house, not one branch
        Assert.Equal(DiscordRouting.DefaultRole(NotificationType.Announcement),
            DiscordRouting.DefaultRole(NotificationType.PublicCaptureReported));
    }

    [Fact]
    public void NewTip_pingsTheConfiguredRoleWhileTheDeskNoticeStaysUnroutable()
    {
        Assert.True(DiscordRouting.IsRoutable(NotificationType.PublicTipCreated));
        Assert.True(DiscordRouting.PingsRole(NotificationType.PublicTipCreated));
        Assert.False(DiscordRouting.PingsRecipients(NotificationType.PublicTipCreated));
        Assert.False(DiscordRouting.PingsNobody(NotificationType.PublicTipCreated));
        Assert.Contains(NotificationType.PublicTipCreated, DiscordRouting.RoleRoutableTypes);
        // the fallback role, i.e. NOOSE - the desk is the whole house, not one branch
        Assert.Equal(DiscordRouting.DefaultRole(NotificationType.Announcement),
            DiscordRouting.DefaultRole(NotificationType.PublicTipCreated));
        // routing either of these would ping on a citizen's reply as well
        Assert.False(DiscordRouting.IsRoutable(NotificationType.PublicTipReceived));
        Assert.False(DiscordRouting.IsRoutable(NotificationType.PublicTipAnswered));
    }

    // ----- DiscordWebhookConfig record -----

    [Fact]
    public void DiscordWebhookConfig_DefaultBaseUrl_isNooseInfo()
    {
        Assert.Equal("https://noose.info", DiscordWebhookConfig.DefaultBaseUrl);
    }

    [Fact]
    public void DiscordWebhookConfig_storesConstructorValues()
    {
        var webhooks = new Dictionary<NotificationType, string?>
        {
            [NotificationType.Announcement] = "https://hook/announce",
        };
        var roles = new Dictionary<NotificationType, string?>
        {
            [NotificationType.Recruiting] = "role-1",
        };

        var config = new DiscordWebhookConfig(true, "https://example.test", webhooks, roles, IncludeHeadline: true);

        Assert.True(config.Enabled);
        Assert.Equal("https://example.test", config.SiteBaseUrl);
        Assert.Same(webhooks, config.Webhooks);
        Assert.Same(roles, config.Roles);
        Assert.True(config.IncludeHeadline);
    }

    // ----- DiscordWebhookConfigInput defaults -----

    [Fact]
    public void DiscordWebhookConfigInput_defaults_areEmptyAndDisabled()
    {
        var input = new DiscordWebhookConfigInput();

        Assert.False(input.Enabled);
        Assert.Null(input.SiteBaseUrl);
        Assert.NotNull(input.Webhooks);
        Assert.Empty(input.Webhooks);
        Assert.NotNull(input.Roles);
        Assert.Empty(input.Roles);
    }
}
