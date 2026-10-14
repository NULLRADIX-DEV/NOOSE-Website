using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Common;

/// <summary>Which notification categories mirror to a shared Discord channel, and how each pings: personal categories ping the recipients, broadcast categories ping a configured role, announcement-only categories ping nobody.</summary>
public static class DiscordRouting
{
    public static readonly IReadOnlyList<NotificationType> RoutableTypes = new[]
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
        // the scheduling event, not AppointmentAssigned: that one is the personal "you are a participant"
        // notice and would post one channel message per participant
        NotificationType.AppointmentScheduled,
        NotificationType.PersonnelEntry,
        // its own channel, separate from PersonnelEntry: a termination is announced to the whole agency, the
        // other personnel entries are house bookkeeping
        NotificationType.AgentTerminated,
        // a ceremony, announced like a personnel entry: its own embed pings the honoured agent. The bell goes
        // through NotifyManyInAppAsync, so no generic post ever reaches this channel
        NotificationType.DecorationAwarded,
        // PublicWantedExpired is deliberately absent: NotificationService.NotifyManyAsync pushes every routable
        // category on its own, so a routable operating category would post each expiry into the public channel
        NotificationType.PublicWantedPublished,
        NotificationType.PublicWantedBountyRaised,
        // the citizen desk's one routable category, and it stays generic: the post says a ticket arrived and
        // links to it, never the subject and never the citizen. Deliberately not PublicTicketOpened — that one
        // is the desk bell and folds a running thread onto itself, so routing it would ping again on a reply
        // whenever the desk happened to be caught up
        NotificationType.PublicTicketCreated,
        // the one public category that is routable without a caveat: an official statement, no citizen named, and the
        // link is a permanent public address
        NotificationType.PublicPressPublished,
        // the second citizen category with a channel, and it earns it for a sharper reason than a ticket does:
        // somebody is holding a wanted person and the house has to hear it now. The post carries the report's own
        // case number and nothing else - naming who is holding a wanted criminal, in a channel players read,
        // invites revenge, and the reporter is not anonymous on this path
        NotificationType.PublicCaptureReported,
        // the desk's role ping for a fresh tip, and the ticket construction is why it is a category of its own:
        // PublicTipReceived stays unroutable (routing it would ping on every citizen reply too), so this key
        // carries the channel and the role id while the post itself is the generic notice plus the login-gated
        // link - no citizen, no subject, no case number. Point it at an internal channel: the arrival time alone
        // is a hint about who just filed
        NotificationType.PublicTipCreated,
        // PublicTipReceived/PublicTipAnswered stay absent: the first would fire on replies as well (see above),
        // and an answer is addressed to exactly one citizen.
        // PublicObjectionReceived/PublicObjectionDecided likewise: the first names a citizen disputing a public
        // accusation, the second is addressed to exactly one of them
        // the daily bundle of collected personal pings: one line per agent with counts, never a title
        NotificationType.PingDigest,
        // pings only who still owes the acknowledgment, not the role: the others already answered
        NotificationType.AcknowledgmentReminder,
        // pings the owner only; the post names neither the search nor a hit, both were found under the owner's rights
        NotificationType.SavedSearchHit,
        // leadership asking one silent agent to check in; the post says no more than that, the bell has the date
        NotificationType.InactivityNudge,
    };

    public static bool IsRoutable(NotificationType type) => RoutableTypes.Contains(type);

    /// <summary>Category whose Discord post pings the specific recipient agents.</summary>
    public static bool PingsRecipients(NotificationType type)
        // MeetingReminder pings the role, not individuals: a per-person mention list in a shared
        // channel is the complement of the absence set and would leak who filed an absence
        => type is NotificationType.Mention or NotificationType.Followup
                or NotificationType.JobAssigned or NotificationType.JobDueSoon
                or NotificationType.PersonnelEntry or NotificationType.PingDigest
                or NotificationType.AcknowledgmentReminder or NotificationType.SavedSearchHit
                or NotificationType.DecorationAwarded
                or NotificationType.InactivityNudge;

    /// <summary>Category whose Discord post pings nobody: the embed carries inert mention chips instead.</summary>
    public static bool PingsNobody(NotificationType type)
        // an official announcement, not a summons: the terminated agent is named in the embed and rendered as a
        // mention chip, but nobody gets a notification about someone else's dismissal
        => type is NotificationType.AgentTerminated;

    /// <summary>Category whose Discord post pings a configured role instead of individuals.</summary>
    public static bool PingsRole(NotificationType type)
        => IsRoutable(type) && !PingsRecipients(type) && !PingsNobody(type);

    /// <summary>Routable categories that ping a role (need a configurable role id).</summary>
    public static readonly IReadOnlyList<NotificationType> RoleRoutableTypes =
        RoutableTypes.Where(PingsRole).ToArray();

    /// <summary>Default role id per broadcast category; admin-overridable per server.</summary>
    public static string DefaultRole(NotificationType type) => type switch
    {
        NotificationType.Recruiting => "1515098218545938442", // HRB
        _ => "1479854499853238475",                            // NOOSE
    };
}

/// <summary>Snapshot of the Discord webhook routing config (master switch, base URL, per-category channel URLs and role mentions).</summary>
public sealed record DiscordWebhookConfig(
    bool Enabled, string SiteBaseUrl,
    IReadOnlyDictionary<NotificationType, string?> Webhooks,
    IReadOnlyDictionary<NotificationType, string?> Roles,
    bool IncludeHeadline)
{
    /// <summary>Default site base for absolute links in Discord messages when unset.</summary>
    public const string DefaultBaseUrl = "https://noose.info";
}

/// <summary>Admin input for the Discord webhook config page.</summary>
public sealed class DiscordWebhookConfigInput
{
    public bool Enabled { get; set; }
    public string? SiteBaseUrl { get; set; }

    /// <summary>Webhook URL per category; empty/null disables that category.</summary>
    public Dictionary<NotificationType, string?> Webhooks { get; set; } = new();

    /// <summary>Role id to ping per broadcast category; empty/null falls back to the default role.</summary>
    public Dictionary<NotificationType, string?> Roles { get; set; } = new();

    /// <summary>Include the record header/title in role-ping posts (Announcement, Recruiting, AppointmentScheduled).</summary>
    public bool IncludeHeadline { get; set; } = true;
}
