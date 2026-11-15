using System.Net;
using NetCord.Rest;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>One live card per alarm: posted, re-pinged and edited.</summary>
public interface IDiscordAlarmCards
{
    /// <summary>Posts the card or re-pings under it; throws only before any ping.</summary>
    Task HandleAsync(DiscordPost post, CancellationToken cancellationToken = default);

    /// <summary>Edits the stored card when it changed; never posts one.</summary>
    Task UpdateAsync(string alarmId, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiscordAlarmCards" />
public sealed class DiscordAlarmCards(
    IServiceScopeFactory scopeFactory,
    IDiscordBotSender sender,
    ILogger<DiscordAlarmCards> logger) : IDiscordAlarmCards
{
    /// <summary>Prefix of the re-ping reply.</summary>
    public const string RepingText = "🔔 Erneut alarmiert: ";

    // fits 2000 chars
    private const int PlainChips = 80;

    // Discord's "Unknown Message"
    private const int UnknownMessage = 10008;

    // worker and loop
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task HandleAsync(DiscordPost post, CancellationToken cancellationToken = default)
    {
        var alarmId = post.AlarmId ?? throw new ArgumentException("Not an alarm post.", nameof(post));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var boards = scope.ServiceProvider.GetRequiredService<DiscordLiveBoards>();
            // stored card wins
            if (await boards.GetAlarmBoardAsync(alarmId, cancellationToken) is { } stored
                && await RefreshAsync(boards, alarmId, stored, cancellationToken))
            {
                await RepingAsync(post, stored.Channel, stored.Message, cancellationToken);
                return;
            }
            await PostCardAsync(boards, post, alarmId, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task UpdateAsync(string alarmId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var boards = scope.ServiceProvider.GetRequiredService<DiscordLiveBoards>();
            if (await boards.GetAlarmBoardAsync(alarmId, cancellationToken) is not { } stored
                || await boards.RenderAlarmAsync(alarmId, cancellationToken) is not { } alarm
                || alarm.Card.Hash == stored.Hash)
            {
                return;
            }
            try
            {
                await DiscordPostWorker.TimedAsync(
                    t => sender.EditAsync(stored.Channel, stored.Message, alarm.Card.Components, t), cancellationToken);
            }
            catch (RestException ex) when (IsGone(ex))
            {
                // next re-ping reposts
                return;
            }
            await boards.SetAlarmBoardAsync(alarmId, stored.Channel, stored.Message, alarm.Card.Hash, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Edits the stored card before the re-ping; false only when the card was deleted.</summary>
    private async Task<bool> RefreshAsync(DiscordLiveBoards boards, string alarmId,
        (ulong Channel, ulong Message, string Hash) stored, CancellationToken cancellationToken)
    {
        if (await boards.RenderAlarmAsync(alarmId, cancellationToken) is not { } alarm)
        {
            return true;
        }
        try
        {
            await DiscordPostWorker.TimedAsync(
                t => sender.EditAsync(stored.Channel, stored.Message, alarm.Card.Components, t), cancellationToken);
        }
        catch (RestException ex) when (IsGone(ex))
        {
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // ping matters more
            logger.LogWarning("Discord alarm card edit failed ({Reason}), re-pinging anyway.", DiscordPostWorker.Reason(ex));
            return true;
        }
        await boards.SetAlarmBoardAsync(alarmId, stored.Channel, stored.Message, alarm.Card.Hash, cancellationToken);
        return true;
    }

    /// <summary>One card that pings everyone with a chip on it; the others follow as plain lines.</summary>
    private async Task PostCardAsync(DiscordLiveBoards boards, DiscordPost post, string alarmId, CancellationToken cancellationToken)
    {
        var rendered = await boards.RenderAlarmAsync(alarmId, cancellationToken);
        var card = rendered?.Card.Components ?? post.Card;
        // chips after FitText
        var shown = DiscordPostWorker.ChipsOf(Text(card)).ToHashSet();
        var onCard = post.PingUsers.Where(shown.Contains).Distinct().ToList();
        var message = await DiscordPostWorker.TimedAsync(
            t => sender.SendAsync(post.ChannelId, card, DiscordPostWorker.Mentions(onCard, null), cancellationToken: t), cancellationToken);
        // card is out
        if (rendered is { } alarm)
        {
            try
            {
                await boards.SetAlarmBoardAsync(alarmId, post.ChannelId, message, alarm.Card.Hash, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Discord alarm card could not be stored ({Error}).", ex.GetType().Name);
            }
        }
        var rest = NotificationCards.PingLines(
            post.PingUsers.Where(u => !shown.Contains(u)).Select(u => u.ToString()).ToList(), PlainChips);
        await DiscordPostWorker.SendPingLinesAsync(sender, logger, post.Category, post.ChannelId,
            new[] { rest.First }.Where(l => l.Length > 0).Concat(rest.Rest), cancellationToken);
    }

    /// <summary>Re-pings the open ones in a reply under the card.</summary>
    private async Task RepingAsync(DiscordPost post, ulong channel, ulong message, CancellationToken cancellationToken)
    {
        var lines = NotificationCards.PingLines(post.PingUsers.Select(u => u.ToString()).ToList(), PlainChips);
        if (lines.First.Length == 0)
        {
            return;
        }
        await DiscordPostWorker.TimedAsync(t => sender.SendTextAsync(channel, RepingText + lines.First,
            DiscordPostWorker.Mentions(DiscordPostWorker.ChipsOf(lines.First), null), message, t), cancellationToken);
        await DiscordPostWorker.SendPingLinesAsync(sender, logger, post.Category, channel, lines.Rest, cancellationToken);
    }

    // deleted by hand
    private static bool IsGone(RestException ex) => ex.StatusCode == HttpStatusCode.NotFound || ex.Error?.Code == UnknownMessage;

    /// <summary>All text of a card, for the chips it carries.</summary>
    private static string Text(IEnumerable<IMessageComponentProperties> card)
    {
        var texts = new List<string>();
        void Walk(IEnumerable<object> items)
        {
            foreach (var item in items)
            {
                switch (item)
                {
                    case TextDisplayProperties text:
                        texts.Add(text.Content);
                        break;
                    case ComponentContainerProperties container:
                        Walk(container.Components);
                        break;
                    case ComponentSectionProperties section:
                        Walk(section.Components);
                        break;
                }
            }
        }
        Walk(card);
        return string.Join('\n', texts);
    }
}
