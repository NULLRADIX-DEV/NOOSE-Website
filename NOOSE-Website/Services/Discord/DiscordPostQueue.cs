using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using NetCord.Rest;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Today's webhook URL and exact JSON bodies.</summary>
public sealed record WebhookFallback(string Url, IReadOnlyList<object> Payloads);

/// <summary>One bot post with its pings and webhook fallback.</summary>
public sealed record DiscordPost(
    NotificationType Category, ulong ChannelId, IReadOnlyList<IMessageComponentProperties> Card,
    IReadOnlyList<ulong> PingUsers, ulong? PingRole, IReadOnlyList<string> ExtraPings,
    WebhookFallback? Fallback, string? AlarmId = null, bool IsReping = false);

/// <summary>Hand-off to the post worker; never blocks, never calls Discord.</summary>
public interface IDiscordPostQueue
{
    /// <summary>Queue one post; open ones are lost on a restart.</summary>
    void Enqueue(DiscordPost post);

    /// <summary>Waits for the next post; alarm posts come before all others.</summary>
    ValueTask<DiscordPost> ReadAsync(CancellationToken cancellationToken);

    /// <summary>The next post if one is waiting, alarm posts first.</summary>
    bool TryRead([NotNullWhen(true)] out DiscordPost? post);
}

/// <inheritdoc cref="IDiscordPostQueue" />
public sealed class DiscordPostQueue : IDiscordPostQueue
{
    private readonly Channel<DiscordPost> _alarms = Channel.CreateUnbounded<DiscordPost>();
    private readonly Channel<DiscordPost> _posts = Channel.CreateUnbounded<DiscordPost>();

    // permit per post
    private readonly SemaphoreSlim _queued = new(0);

    // unbounded, never full
    public void Enqueue(DiscordPost post)
    {
        (post.AlarmId is null ? _posts : _alarms).Writer.TryWrite(post);
        _queued.Release();
    }

    public async ValueTask<DiscordPost> ReadAsync(CancellationToken cancellationToken)
    {
        await _queued.WaitAsync(cancellationToken);
        return Take();
    }

    public bool TryRead([NotNullWhen(true)] out DiscordPost? post)
    {
        if (!_queued.Wait(0))
        {
            post = null;
            return false;
        }
        post = Take();
        return true;
    }

    // permit means post
    private DiscordPost Take()
        => _alarms.Reader.TryRead(out var alarm) ? alarm
            : _posts.Reader.TryRead(out var post) ? post
            : throw new InvalidOperationException("The post queue lost a post.");
}

/// <summary>Sends and edits bot messages; the NetCord seam.</summary>
public interface IDiscordBotSender
{
    /// <summary>Sends a components-v2 card and returns its message id.</summary>
    Task<ulong> SendAsync(ulong channelId, IReadOnlyList<IMessageComponentProperties> card, AllowedMentionsProperties mentions,
        ulong? replyTo = null, CancellationToken cancellationToken = default);

    /// <summary>Sends a plain text message, for ping lines under a card.</summary>
    Task SendTextAsync(ulong channelId, string content, AllowedMentionsProperties mentions,
        ulong? replyTo = null, CancellationToken cancellationToken = default);

    /// <summary>Replaces the card of a message without pinging anyone.</summary>
    Task EditAsync(ulong channelId, ulong messageId, IReadOnlyList<IMessageComponentProperties> card,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes a bot message.</summary>
    Task DeleteAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default);
}

/// <summary>Tells a channel problem (no access, no such channel) from a passing failure.</summary>
public static class DiscordSendFailure
{
    // channel error codes
    private static readonly int[] ChannelCodes = [10003, 50001, 50013, 50083];

    /// <summary>True for 403, 404 and Discord's channel codes.</summary>
    public static bool IsChannelProblem(int status, int? code)
        => status is 403 or 404 || (code is { } c && ChannelCodes.Contains(c));

    /// <summary>Webhook to fall back to when the derived board channel fails.</summary>
    public static string? FactionBoardFallback(ulong? explicitChannel, string? threatBoardWebhook, int status, int? code)
        => explicitChannel is null && !string.IsNullOrWhiteSpace(threatBoardWebhook) && IsChannelProblem(status, code)
            ? threatBoardWebhook
            : null;
}
