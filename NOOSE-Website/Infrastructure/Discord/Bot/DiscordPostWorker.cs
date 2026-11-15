using System.Net.Http.Json;
using System.Text.RegularExpressions;
using NetCord.Rest;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>Sends queued bot posts, alarms first, webhook as fallback.</summary>
public sealed partial class DiscordPostWorker(
    IDiscordPostQueue queue,
    IDiscordBotSender sender,
    IDiscordBotRoutes routes,
    IHttpClientFactory httpFactory,
    IDiscordAlarmCards alarmCards,
    ILogger<DiscordPostWorker> logger) : BackgroundService
{
    // Discord's user limit
    private const int MaxMentions = 100;
    private static readonly TimeSpan DrainFor = TimeSpan.FromSeconds(10);

    /// <summary>A hanging call must not hold up the alarms behind it.</summary>
    internal static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (true)
            {
                var post = await queue.ReadAsync(stoppingToken);
                // finish started post
                await DeliverAsync(post, CancellationToken.None);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* ignore */
        }
    }

    /// <summary>Sends what is still queued for a few seconds, alarms first; the rest is lost.</summary>
    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        using var drain = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drain.CancelAfter(DrainFor);
        while (!drain.IsCancellationRequested && queue.TryRead(out var post))
        {
            await DeliverAsync(post, drain.Token);
        }
    }

    /// <summary>One post; a failure never escapes and never ends the worker.</summary>
    private async Task DeliverAsync(DiscordPost post, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await SendAsync(post, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                await FallBackAsync(post, ex, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            /* ignore */
        }
        catch (Exception ex)
        {
            logger.LogError("Discord post for {Type} failed unexpectedly ({Error}).", post.Category, ex.GetType().Name);
        }
    }

    /// <summary>Throws only while the card is not out; the ping lines after it fail one by one.</summary>
    private async Task SendAsync(DiscordPost post, CancellationToken cancellationToken)
    {
        if (post.AlarmId is not null)
        {
            await alarmCards.HandleAsync(post, cancellationToken);
            return;
        }
        await TimedAsync(t => sender.SendAsync(post.ChannelId, post.Card, Mentions(post.PingUsers, post.PingRole), cancellationToken: t),
            cancellationToken);
        await SendPingLinesAsync(sender, logger, post.Category, post.ChannelId, post.ExtraPings, cancellationToken);
    }

    /// <summary>Posts by webhook what the bot could not send.</summary>
    private async Task FallBackAsync(DiscordPost post, Exception failure, CancellationToken cancellationToken)
    {
        if (post.Fallback is not { } fallback)
        {
            logger.LogWarning("Discord bot post for {Type} failed ({Reason}), no webhook to fall back to.", post.Category, Reason(failure));
            return;
        }
        if (failure is RestException problem && DiscordSendFailure.IsChannelProblem((int)problem.StatusCode, problem.Error?.Code))
        {
            routes.MarkFailing(fallback.Url);
        }
        logger.LogWarning("Discord bot post for {Type} failed ({Reason}), using the webhook.", post.Category, Reason(failure));

        var client = httpFactory.CreateClient("discord");
        foreach (var payload in fallback.Payloads)
        {
            try
            {
                using var response = await client.PostAsJsonAsync(fallback.Url, payload, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    logger.LogWarning("Discord webhook fallback for {Type} returned {Status}.", post.Category, (int)response.StatusCode);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Discord webhook fallback for {Type} failed ({Error}).", post.Category, ex.GetType().Name);
            }
        }
    }

    /// <summary>Ping lines under a sent card; failures are only logged.</summary>
    internal static async Task SendPingLinesAsync(IDiscordBotSender sender, ILogger logger, Models.Enums.NotificationType category,
        ulong channelId, IEnumerable<string> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines)
        {
            try
            {
                await TimedAsync(t => sender.SendTextAsync(channelId, line, Mentions(ChipsOf(line), null), cancellationToken: t),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Discord ping line for {Type} failed ({Reason}), the card is out.", category, Reason(ex));
            }
        }
    }

    /// <summary>One bot call with its own time limit.</summary>
    internal static async Task<T> TimedAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(SendTimeout);
        return await call(limit.Token);
    }

    /// <inheritdoc cref="TimedAsync{T}" />
    internal static Task TimedAsync(Func<CancellationToken, Task> call, CancellationToken cancellationToken)
        => TimedAsync(async t =>
        {
            await call(t);
            return true;
        }, cancellationToken);

    /// <summary>Status and Discord code, never the message (it may echo the URL).</summary>
    internal static string Reason(Exception failure)
        => failure is RestException rest ? $"{(int)rest.StatusCode}/{rest.Error?.Code.ToString() ?? "-"}" : failure.GetType().Name;

    /// <summary>Allow-list of exactly these users and this role; empty lists, never null.</summary>
    internal static AllowedMentionsProperties Mentions(IEnumerable<ulong> users, ulong? role) => new()
    {
        // default allows everyone
        Everyone = false,
        AllowedUsers = users.Distinct().Take(MaxMentions).ToList(),
        AllowedRoles = role is { } id ? [id] : [],
    };

    /// <summary>The users one text mentions.</summary>
    internal static IEnumerable<ulong> ChipsOf(string text)
        => UserMention().Matches(text).Select(m => ulong.TryParse(m.Groups[1].ValueSpan, out var id) ? id : 0).Where(id => id != 0);

    // role pings excluded
    [GeneratedRegex(@"<@(\d{1,20})>")]
    private static partial Regex UserMention();
}
