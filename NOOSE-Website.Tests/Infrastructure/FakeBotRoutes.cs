using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Bot routes with one fixed answer; the bot is off unless a test sets <see cref="Route"/>.</summary>
public sealed class FakeBotRoutes : IDiscordBotRoutes
{
    public BotRoute Route { get; set; } = new(BotRouteState.BotOff, null);

    /// <summary>The alarm card's channel; null keeps the alarm on the webhook.</summary>
    public ulong? AlarmChannel { get; set; }

    /// <summary>The faction board's channel; null keeps the board on the webhook.</summary>
    public ulong? FactionBoard { get; set; }

    /// <summary>The webhook URLs asked about, exactly as given.</summary>
    public List<string?> Asked { get; } = [];

    /// <summary>Thrown by every lookup when set.</summary>
    public Exception? Failure { get; set; }

    public Task<BotRoute> ForWebhookAsync(string? webhookUrl, CancellationToken cancellationToken = default)
    {
        Asked.Add(webhookUrl);
        return Failure is { } failure ? Task.FromException<BotRoute>(failure) : Task.FromResult(Route);
    }

    public void MarkFailing(string webhookUrl) => throw new NotSupportedException();

    public Task<ulong?> AlarmChannelAsync(CancellationToken cancellationToken = default)
        => Failure is { } failure ? Task.FromException<ulong?>(failure) : Task.FromResult(AlarmChannel);

    public Task<ulong?> FactionBoardChannelAsync(CancellationToken cancellationToken = default)
        => Failure is { } failure ? Task.FromException<ulong?>(failure) : Task.FromResult(FactionBoard);
}
