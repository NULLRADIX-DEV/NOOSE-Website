using System.Security.Claims;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Notifications;

namespace NOOSE_Website.Services;

/// <summary>In-app notifications (bell). Best-effort: created in a separate context after the action so a failed notification never rolls back the core action.</summary>
public interface INotificationService
{
    /// <summary>Create a notification for one recipient (no-op on empty recipient id).</summary>
    Task NotifyAsync(string? recipientId, NotificationType type, string title, string? href,
        CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="NotifyAsync"/>, but folds onto the recipient's still-unread notice for the same type and href instead of adding a second row: one bell entry per target until it is read.</summary>
    Task NotifyOnceAsync(string? recipientId, NotificationType type, string title, string? href,
        CancellationToken cancellationToken = default);

    /// <summary>Notify agents mentioned via @{Agent:Id} in the text, excluding the trigger, deduplicated and visibility-filtered. Title stays generic. <paramref name="childType"/> names the child the text sits in; partners are never pinged from a child, whose release is not known here.</summary>
    Task NotifyMentionedAsync(string? text, string title, string? href, string targetType, string targetId,
        ClaimsPrincipal trigger, CancellationToken cancellationToken = default, string? childType = null);

    /// <summary>Same as <see cref="NotifyMentionedAsync"/> but only for mentions added since the old text, so re-saving an edited field never pings twice.</summary>
    Task NotifyMentionedDeltaAsync(string? oldText, string? newText, string title, string? href, string targetType,
        string targetId, ClaimsPrincipal trigger, CancellationToken cancellationToken = default, string? childType = null);

    /// <summary>Broadcast the same notification to many recipients, excluding the trigger and deduplicated; empty list = no-op.</summary>
    Task NotifyManyAsync(IReadOnlyCollection<string> recipientIds, NotificationType type, string title,
        string? href, string? triggerId, CancellationToken cancellationToken = default);

    /// <summary>Like <see cref="NotifyManyAsync"/>, but folds onto each recipient's still-unread notice for the same type and href; only genuinely new notices reach the Discord channel.</summary>
    Task NotifyManyOnceAsync(IReadOnlyCollection<string> recipientIds, NotificationType type, string title,
        string? href, string? triggerId, CancellationToken cancellationToken = default);

    /// <summary>Caller's latest notifications, newest first.</summary>
    Task<List<Notification>> GetOwnAsync(ClaimsPrincipal actor, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>Caller's unread notification count (badge).</summary>
    Task<int> GetUnreadCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Mark one of the caller's notifications as read.</summary>
    Task AsReadMarkAsync(string notificationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Mark all of the caller's unread notifications as read.</summary>
    Task AllAsReadAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>One filtered page of the caller's own notifications, newest first.</summary>
    Task<NotificationInboxPage> GetInboxAsync(ClaimsPrincipal actor, NotificationInboxQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>The types the caller has received, with their counts; feeds the inbox filter.</summary>
    Task<List<NotificationTypeCount>> GetOwnTypesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Mark one of the caller's read notifications as unread again.</summary>
    /// <remarks>It counts as open again: the watchlist fan-out skips its record while it is unread, and a folding
    /// notice (tickets) lands on it when it is the newest unread one for that target.</remarks>
    Task AsUnreadMarkAsync(string notificationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
