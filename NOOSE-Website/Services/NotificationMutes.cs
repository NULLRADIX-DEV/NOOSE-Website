using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Which notification categories an agent may silence, and how the profile groups them.</summary>
/// <remarks>
/// A silenced notice is still delivered, only already read: it stays in the inbox and never turns the bell red.
/// The one place that decides this; <see cref="NotificationService"/> and the profile panel both ask here.
/// </remarks>
public static class NotificationMutes
{
    /// <summary>A heading on the profile and the categories under it, in display order.</summary>
    public sealed record Group(string Label, string Icon, IReadOnlyList<NotificationType> Types);

    /// <summary>Always ring: someone addressing you directly, and changes to your own account.</summary>
    public static readonly IReadOnlyList<NotificationType> Locked =
    [
        NotificationType.Mention,
        NotificationType.Account,
        // leadership asking a silent agent to check in; muting it would defeat the only channel left
        NotificationType.InactivityNudge,
    ];

    /// <summary>Discord-only categories; they never reach the bell, so there is nothing to silence.</summary>
    public static readonly IReadOnlyList<NotificationType> ChannelOnly =
    [
        NotificationType.PublicTicketCreated,
        NotificationType.AgentTerminated,
        NotificationType.PublicTipCreated,
        // posted straight to the channel, never written as a notice
        NotificationType.PersonnelEntry,
        NotificationType.PublicWantedPublished,
        NotificationType.PublicWantedBountyRaised,
        NotificationType.PublicPressPublished,
        NotificationType.PingDigest,
        NotificationType.FactionThreatBoard,
    ];

    /// <summary>The switchable categories as the profile shows them.</summary>
    public static readonly IReadOnlyList<Group> Groups =
    [
        new("Meine Arbeit", MudBlazor.Icons.Material.Filled.AssignmentInd,
        [
            NotificationType.JobAssigned,
            NotificationType.JobDueSoon,
            NotificationType.Followup,
            NotificationType.RecordModified,
            NotificationType.SavedSearchHit,
            NotificationType.TestAssigned,
            NotificationType.TestDueReminder,
            NotificationType.TestGraded,
            NotificationType.RequestDecided,
            NotificationType.ThreatSpike,
            NotificationType.HandoverReceived,
        ]),
        new("Termine und Besprechungen", MudBlazor.Icons.Material.Filled.Event,
        [
            NotificationType.AppointmentAssigned,
            NotificationType.AppointmentScheduled,
            NotificationType.MeetingScheduled,
            NotificationType.MeetingReminder,
            NotificationType.PresentationLive,
        ]),
        new("Behörde und Personal", MudBlazor.Icons.Material.Filled.Apartment,
        [
            NotificationType.Announcement,
            NotificationType.AcknowledgmentReminder,
            NotificationType.SituationReport,
            NotificationType.AbsenceFiled,
            NotificationType.AbductionFiled,
            NotificationType.Recruiting,
            NotificationType.TestSubmitted,
            NotificationType.Financing,
            NotificationType.Feedback,
            NotificationType.PartnerInquiry,
            NotificationType.CounterIntelFinding,
            NotificationType.NooseiAnomaly,
            NotificationType.DecorationAwarded,
        ]),
        new("Öffentlichkeit und Bürgerkontakt", MudBlazor.Icons.Material.Filled.Public,
        [
            NotificationType.PublicWantedExpired,
            NotificationType.PublicCaptureReported,
            NotificationType.PublicTipReceived,
            NotificationType.PublicTipAnswered,
            NotificationType.PublicRewardPaid,
            NotificationType.PublicTicketOpened,
            NotificationType.PublicTicketAnswered,
            NotificationType.PublicTicketInternal,
            NotificationType.PublicObjectionReceived,
            NotificationType.PublicObjectionDecided,
        ]),
    ];

    private static readonly HashSet<NotificationType> Mutable = Groups.SelectMany(g => g.Types).ToHashSet();

    /// <summary>Whether an agent may silence this category.</summary>
    public static bool IsMutable(NotificationType type) => Mutable.Contains(type);

    /// <summary>Whether the agent switched this category off; locked categories never count as muted.</summary>
    public static bool IsMuted(NavPreferences prefs, NotificationType type)
        => IsMutable(type) && prefs.MutedNotifications.Contains(type.ToString());
}
