using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Posts a generic notice plus a login-gated link to a per-category Discord channel, pinging the recipients (personal categories) or a configured role (broadcast categories). Best-effort: never throws into the caller.</summary>
public interface IDiscordWebhookService
{
    /// <summary>Post to the category's channel and ping the recipients or the configured role; no-op if disabled, unroutable, or no URL configured. For Announcement/Recruiting the optional <paramref name="headline"/> is included when the header toggle is on, otherwise a generic notice is used.</summary>
    Task PushAsync(NotificationType type, string? href, IReadOnlyCollection<string>? recipientAgentIds, string? headline = null, CancellationToken cancellationToken = default);

    /// <summary>Post a personnel-file entry as a rich "EINTRAG" embed to the PersonnelEntry channel, pinging the subject agent. Best-effort; no-op if disabled or no URL configured.</summary>
    Task PushPersonnelEntryAsync(string subjectAgentId, string subjectDisplay, string artLabel,
        DateTime entryDate, string reasonPlain, IReadOnlyList<string> executorDisplays,
        string? href, CancellationToken cancellationToken = default);

    /// <summary>Post a decoration as a golden "EHRUNG" embed to the DecorationAwarded channel, pinging the recipient. Names the codename only. Best-effort; no-op if disabled or no URL configured.</summary>
    Task PushDecorationAsync(string recipientAgentId, string recipientCodename, string decorationName,
        DateTime awardedOn, string citationPlain, string? awardedByName, string? href,
        CancellationToken cancellationToken = default);

    /// <summary>Post a termination as a rich "KÜNDIGUNG" embed to the AgentTerminated channel. Pings nobody — the subject and the executor appear as inert mention chips inside the embed. Best-effort; no-op if disabled or no URL configured.</summary>
    Task PushTerminationAsync(string subjectAgentId, string subjectDisplay, string? executorAgentId,
        string? executorDisplay, DateTime terminatedAt, string reasonPlain, string? href,
        CancellationToken cancellationToken = default);

    /// <summary>Current routing config (fresh read) for the admin page.</summary>
    Task<DiscordWebhookConfig> GetConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>Persist the routing config; admin-only.</summary>
    Task SaveConfigAsync(DiscordWebhookConfigInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Send a test message to a category's channel; admin-only. Returns true on 2xx.</summary>
    Task<bool> TestAsync(NotificationType type, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Post one daily-digest message to the PingDigest channel. The content carries its own user mentions; only <paramref name="discordUserIds"/> may resolve. Best-effort; returns true on 2xx.</summary>
    Task<bool> PushDigestAsync(string content, IReadOnlyList<string> discordUserIds, string? href = null, CancellationToken cancellationToken = default);

    /// <summary>Post an app-authored message to a category's channel with that category's ping (role or none) and an optional login-gated link. Best-effort; returns true on 2xx.</summary>
    Task<bool> PushCustomAsync(NotificationType type, string content, string? href = null, CancellationToken cancellationToken = default);

    /// <summary>Post the threat board as a new message and return its Discord id; null when Discord refused. Pings nobody.</summary>
    Task<string?> PostThreatBoardAsync(string webhookUrl, ThreatBoardEmbed embed, CancellationToken cancellationToken = default);

    /// <summary>Edit the threat board message in place. <see cref="ThreatBoardEdit.Missing"/> means Discord no longer has it.</summary>
    Task<ThreatBoardEdit> EditThreatBoardAsync(string webhookUrl, string messageId, ThreatBoardEmbed embed, CancellationToken cancellationToken = default);

    /// <summary>Delete a threat board message the settings no longer point at.</summary>
    Task<ThreatBoardRemoval> DeleteThreatBoardAsync(string webhookUrl, string messageId, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of deleting an old threat board message.</summary>
public enum ThreatBoardRemoval
{
    /// <summary>Deleted, or Discord no longer had it.</summary>
    Removed,

    /// <summary>The URL may no longer touch it (new token, archived thread); nothing more to try.</summary>
    Unreachable,

    /// <summary>Rate limit or outage; retry on the next pass.</summary>
    Failed,
}

/// <summary>Outcome of editing the threat board message.</summary>
public enum ThreatBoardEdit
{
    Edited,

    /// <summary>Deleted in Discord or the webhook is gone; a fresh post is due.</summary>
    Missing,

    /// <summary>Its thread was archived; a fresh post reopens the thread, after which the old message can go.</summary>
    Archived,

    /// <summary>Rate limit, outage or a refused body; retry later, never repost (that would duplicate the board).</summary>
    Failed,
}
