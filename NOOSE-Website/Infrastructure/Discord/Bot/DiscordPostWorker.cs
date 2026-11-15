using System.Net.Http.Json;
using System.Text.RegularExpressions;
using NetCord.Rest;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>Sends queued posts as bot cards, alarms first; a failed send goes to the webhook instead.</summary>
public sealed partial class DiscordPostWorker(
    IDiscordPostQueue queue,
    IDiscordBotSender sender,
    IDiscordBotRoutes routes,
    IHttpClientFactory httpFactory,
    ILogger<DiscordPostWorker> logger) : BackgroundService
{
    // Discord's user limit
    private const int MaxMentions = 100;
    private static readonly TimeSpan DrainFor = TimeSpan.FromSeconds(10);

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
            /* stopping */
        }
        catch (Exception ex)
        {
            logger.LogError("Discord post for {Type} failed unexpectedly ({Error}).", post.Category, ex.GetType().Name);
        }
    }

    private async Task SendAsync(DiscordPost post, CancellationToken cancellationToken)
    {
        await sender.SendAsync(post.ChannelId, post.Card, Mentions(post.PingUsers, post.PingRole), cancellationToken: cancellationToken);
        foreach (var line in post.ExtraPings)
        {
            await sender.SendTextAsync(post.ChannelId, line, Mentions(ChipsOf(line), null), cancellationToken: cancellationToken);
        }
    }

    /// <summary>The webhook posts what the bot could not; a channel problem keeps the next posts there for a while.</summary>
    private async Task FallBackAsync(DiscordPost post, Exception failure, CancellationToken cancellationToken)
    {
        // status only
        var reason = failure is RestException rest ? $"{(int)rest.StatusCode}/{rest.Error?.Code.ToString() ?? "-"}" : failure.GetType().Name;
        if (post.Fallback is not { } fallback)
        {
            logger.LogWarning("Discord bot post for {Type} failed ({Reason}), no webhook to fall back to.", post.Category, reason);
            return;
        }
        if (failure is RestException problem && DiscordSendFailure.IsChannelProblem((int)problem.StatusCode, problem.Error?.Code))
        {
            routes.MarkFailing(fallback.Url);
        }
        logger.LogWarning("Discord bot post for {Type} failed ({Reason}), using the webhook.", post.Category, reason);

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

    /// <summary>Allow-list of exactly these users and this role; empty lists, never null.</summary>
    private static AllowedMentionsProperties Mentions(IEnumerable<ulong> users, ulong? role) => new()
    {
        // default allows everyone
        Everyone = false,
        AllowedUsers = users.Distinct().Take(MaxMentions).ToList(),
        AllowedRoles = role is { } id ? [id] : [],
    };

    /// <summary>The users one ping line mentions.</summary>
    private static IEnumerable<ulong> ChipsOf(string line)
        => UserMention().Matches(line).Select(m => ulong.TryParse(m.Groups[1].ValueSpan, out var id) ? id : 0).Where(id => id != 0);

    // role pings excluded
    [GeneratedRegex(@"<@(\d{1,20})>")]
    private static partial Regex UserMention();
}
