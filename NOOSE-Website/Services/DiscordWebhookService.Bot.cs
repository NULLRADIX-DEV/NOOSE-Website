using Microsoft.EntityFrameworkCore;
using NetCord.Rest;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Services;

/// <summary>The bot side of the webhook service: which posts go into the bot queue and how.</summary>
public partial class DiscordWebhookService
{
    // one post per batch
    private static IReadOnlyList<MentionSpec> Batches(Pings pings, int bodyLength)
    {
        if (pings.Role is { } roleId)
        {
            return [MentionSpec.Role(roleId)];
        }
        return pings.Users.Count > 0
            ? MentionBatches(pings.Users, bodyLength).Select(batch => MentionSpec.Users(batch)).ToList()
            : [MentionSpec.None];
    }

    // null: webhook posts
    private async Task<ulong?> BotChannelAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var route = await routes.ForWebhookAsync(url, cancellationToken);
            return route is { State: BotRouteState.Bot, ChannelId: { } channel } ? channel : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            /* best effort */
            logger.LogWarning("Discord bot route failed ({Error}).", ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Queues one bot post: the card's ping line and the extra ping lines come from the same recipient list.</summary>
    private void EnqueueCard(NotificationType type, ulong channelId, string url, IReadOnlyList<object> payloads,
        Pings pings, Func<string, IReadOnlyList<IMessageComponentProperties>> card)
    {
        // ulong range only
        var users = pings.Users.Where(id => ulong.TryParse(id, out _)).ToList();
        var lines = NotificationCards.PingLines(users);
        var role = ulong.TryParse(pings.Role, out var parsed) ? parsed : (ulong?)null;
        var ping = role is null ? lines.First : NotificationCards.RolePing(pings.Role!);
        queue.Enqueue(new DiscordPost(type, channelId, card(ping), users.Select(ulong.Parse).ToList(), role, lines.Rest,
            new WebhookFallback(url, payloads)));
    }

    // ack button gate
    private async Task<bool> NeedsAcknowledgmentAsync(string? href, CancellationToken cancellationToken)
    {
        if (NotificationCards.RecordId(NotificationType.Announcement, href) is not { } id)
        {
            return false;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Announcements.AsNoTracking().AnyAsync(a => a.Id == id && a.AcknowledgmentRequired, cancellationToken);
    }

    // users or role
    private readonly record struct Pings(IReadOnlyList<string> Users, string? Role)
    {
        public static Pings None => new([], null);
    }
}
