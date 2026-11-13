using System.Threading.Channels;
using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>Registers the commands and the bot's identity once the gateway is ready.</summary>
public sealed class DiscordReadyHandler(DiscordCommandRegistrar registrar, DiscordBotStatus status,
    ILogger<DiscordReadyHandler> logger) : IReadyGatewayHandler
{
    public ValueTask HandleAsync(ReadyEventArgs arg)
    {
        status.Connected = true;
        status.ReadyAt = DateTime.UtcNow;
        status.BotName = arg.User.Username;
        status.ApplicationId = arg.ApplicationId;
        status.GuildCount = arg.GuildIds.Count;
        _ = Task.Run(async () =>
        {
            try
            {
                await registrar.RegisterAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Discord command registration failed ({Error}).", ex.GetType().Name);
            }
        });
        return ValueTask.CompletedTask;
    }
}

/// <summary>Bulk-overwrites the global commands; also the settings page's button.</summary>
public sealed class DiscordCommandRegistrar(GatewayClient client, DiscordBotStatus status)
{
    /// <summary>On demand from the settings page: admins only.</summary>
    public Task<int> RegisterAsync(System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct)
    {
        Services.Permission.RequireWriteAccess(actor);
        Services.Permission.RequireAdmin(actor);
        return RegisterAsync(ct);
    }

    /// <summary>At gateway start, no actor.</summary>
    public async Task<int> RegisterAsync(CancellationToken ct)
    {
        var applicationId = status.ApplicationId ?? (await client.Rest.GetCurrentApplicationAsync(cancellationToken: ct)).Id;
        var registered = await client.Rest.BulkOverwriteGlobalApplicationCommandsAsync(applicationId, BotCommandCatalog.All(), cancellationToken: ct);
        status.RegisteredCommands = registered.Count;
        status.RegisteredAt = DateTime.UtcNow;
        status.LastError = null;
        return registered.Count;
    }
}

/// <summary>Presence, live wanted board and live alarm boards; one per host instance like the other workers.</summary>
public sealed class DiscordBotWorker(
    GatewayClient client,
    IServiceScopeFactory scopeFactory,
    EmergencyAlarmBroadcaster alarms,
    DiscordBotStatus status,
    ILogger<DiscordBotWorker> logger) : BackgroundService
{
    private static readonly TimeSpan PresenceEvery = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan WantedEvery = TimeSpan.FromMinutes(2);

    private readonly System.Threading.Channels.Channel<string> _alarmQueue = System.Threading.Channels.Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    private string? _presence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        void OnAlarm(string id) => _alarmQueue.Writer.TryWrite(id);
        alarms.Modified += OnAlarm;
        try
        {
            await Task.WhenAll(LoopAsync(PresenceEvery, UpdatePresenceAsync, stoppingToken),
                LoopAsync(WantedEvery, UpdateWantedBoardAsync, stoppingToken),
                AlarmLoopAsync(stoppingToken));
        }
        finally
        {
            alarms.Modified -= OnAlarm;
        }
    }

    private async Task LoopAsync(TimeSpan every, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        // let the gateway connect first
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                if ((await scope.ServiceProvider.GetRequiredService<IDiscordBotSettingsService>().GetAsync(stoppingToken)).Enabled)
                {
                    await work(scope.ServiceProvider, stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                status.LastError = ex.GetType().Name;
                logger.LogWarning("Discord bot worker step failed ({Error}).", ex.GetType().Name);
            }
            await Task.Delay(every, stoppingToken);
        }
    }

    private async Task UpdatePresenceAsync(IServiceProvider services, CancellationToken ct)
    {
        var state = await services.GetRequiredService<IPublicSituationService>().GetPublishedAsync(ct);
        var openAlarms = await services.GetRequiredService<DiscordLiveBoards>().CountOpenAlarmsAsync(ct);
        var text = openAlarms > 0
            ? $"🚨 Einsatzalarm aktiv ({openAlarms})"
            : state is null ? "fibhq.com · /fib" : $"{DiscordCards.SituationEmoji(state.Level)} DEFCON {DiscordCards.Defcon(state.Level)} · {Models.Enums.PublicSituationLevelDisplay.Name(state.Level)}";
        if (text == _presence)
        {
            return;
        }
        await client.UpdatePresenceAsync(new PresenceProperties(openAlarms > 0 ? UserStatusType.DoNotDisturb : UserStatusType.Online)
        {
            Activities = [new UserActivityProperties("FIB", UserActivityType.Custom) { State = text }],
        }, cancellationToken: ct);
        _presence = text;
    }

    private async Task UpdateWantedBoardAsync(IServiceProvider services, CancellationToken ct)
    {
        var settings = await services.GetRequiredService<IDiscordBotSettingsService>().GetAsync(ct);
        var boards = services.GetRequiredService<DiscordLiveBoards>();
        var stored = await boards.GetWantedBoardAsync(ct);

        // the channel changed or was cleared: the old board goes
        if (stored is { } old && old.Channel != settings.WantedChannelId)
        {
            try
            {
                await client.Rest.DeleteMessageAsync(old.Channel, old.Message, cancellationToken: ct);
            }
            catch { /* already gone */ }
            await boards.ClearWantedBoardAsync(ct);
            stored = null;
        }
        if (settings.WantedChannelId is not { } channel)
        {
            return;
        }

        var card = await boards.RenderWantedAsync(ct);
        if (stored is { } current)
        {
            if (current.Hash == card.Hash)
            {
                return;
            }
            try
            {
                await client.Rest.ModifyMessageAsync(current.Channel, current.Message, m =>
                {
                    m.Components = card.Components;
                    m.Flags = MessageFlags.IsComponentsV2;
                    m.AllowedMentions = AllowedMentionsProperties.None;
                }, cancellationToken: ct);
                await boards.SetWantedBoardAsync(current.Channel, current.Message, card.Hash, ct);
                return;
            }
            catch (RestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound || ex.Error?.Code == 10008)
            {
                // deleted by hand: post a new one
            }
        }
        var message = await client.Rest.SendMessageAsync(channel, new MessageProperties
        {
            Components = card.Components,
            Flags = MessageFlags.IsComponentsV2,
            AllowedMentions = AllowedMentionsProperties.None,
        }, cancellationToken: ct);
        try
        {
            await client.Rest.PinMessageAsync(channel, message.Id, cancellationToken: ct);
        }
        catch { /* no pin right */ }
        await boards.SetWantedBoardAsync(channel, message.Id, card.Hash, ct);
    }

    private async Task AlarmLoopAsync(CancellationToken stoppingToken)
    {
        await foreach (var first in _alarmQueue.Reader.ReadAllAsync(stoppingToken))
        {
            // answers come in bursts: collect them for a moment
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            var ids = new HashSet<string> { first };
            while (_alarmQueue.Reader.TryRead(out var next))
            {
                ids.Add(next);
            }
            foreach (var id in ids)
            {
                try
                {
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var settings = await scope.ServiceProvider.GetRequiredService<IDiscordBotSettingsService>().GetAsync(stoppingToken);
                    if (settings.Enabled)
                    {
                        await UpdateAlarmBoardAsync(scope.ServiceProvider.GetRequiredService<DiscordLiveBoards>(), settings, id, stoppingToken);
                    }
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    logger.LogWarning("Discord alarm board failed ({Error}).", ex.GetType().Name);
                }
            }
        }
    }

    private async Task UpdateAlarmBoardAsync(DiscordLiveBoards boards, DiscordBotSettings settings, string alarmId, CancellationToken ct)
    {
        var rendered = await boards.RenderAlarmAsync(alarmId, ct);
        if (rendered is not { } alarm)
        {
            return;
        }
        var stored = await boards.GetAlarmBoardAsync(alarmId, ct);
        if (stored is { } current)
        {
            if (current.Hash == alarm.Card.Hash)
            {
                return;
            }
            await client.Rest.ModifyMessageAsync(current.Channel, current.Message, m =>
            {
                m.Components = alarm.Card.Components;
                m.Flags = MessageFlags.IsComponentsV2;
                m.AllowedMentions = AllowedMentionsProperties.None;
            }, cancellationToken: ct);
            await boards.SetAlarmBoardAsync(alarmId, current.Channel, current.Message, alarm.Card.Hash, ct);
            return;
        }
        // a new board only for a fresh, open alarm
        if (settings.AlarmChannelId is not { } channel || !alarm.IsOpen || DateTime.UtcNow - alarm.TriggeredAt > TimeSpan.FromMinutes(5))
        {
            return;
        }
        var message = await client.Rest.SendMessageAsync(channel, new MessageProperties
        {
            Components = alarm.Card.Components,
            Flags = MessageFlags.IsComponentsV2,
            AllowedMentions = AllowedMentionsProperties.None,
        }, cancellationToken: ct);
        await boards.SetAlarmBoardAsync(alarmId, channel, message.Id, alarm.Card.Hash, ct);
    }
}
