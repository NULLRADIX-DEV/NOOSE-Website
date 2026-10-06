using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <summary>Mention fan-out for record fields. Always pass the HOST record as entityType/entityId — Visibility treats an unknown type as visible, so a child type would silently drop the classification gate.</summary>
public static class MentionNotify
{
    /// <summary>Joins several free-text fields of one record into a single scan target.</summary>
    public static string Scope(params string?[] fields) => string.Join(" ", fields);

    /// <summary>Pings only agents mentioned in <paramref name="newText"/> but not in <paramref name="oldText"/>; pass null as old text on create. Never throws.</summary>
    public static async Task DeltaAsync(INotificationService notifications, string? oldText, string? newText,
        string what, string entityType, string entityId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default, string? href = null, string? childType = null)
    {
        try
        {
            var who = string.IsNullOrWhiteSpace(actor.GetCodename()) ? "Ein Agent" : actor.GetCodename();
            await notifications.NotifyMentionedDeltaAsync(oldText, newText, $"{who} hat dich in {what} erwähnt.",
                href ?? SearchNavigation.For(entityType, entityId), entityType, entityId, actor, cancellationToken, childType);
        }
        catch { /* best effort */ }
    }
}
