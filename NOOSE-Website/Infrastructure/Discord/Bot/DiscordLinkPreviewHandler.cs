using NetCord;
using NetCord.Gateway;
using NetCord.Hosting.Gateway;
using NetCord.Rest;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Infrastructure.Discord.Bot;

/// <summary>A record link posted by an agent gets the record card as a reply, computed with the poster's rights.</summary>
public sealed class DiscordLinkPreviewHandler(
    IServiceScopeFactory scopeFactory,
    DiscordBotRateLimiter rateLimiter,
    ILogger<DiscordLinkPreviewHandler> logger) : IMessageCreateGatewayHandler
{
    public ValueTask HandleAsync(Message message)
    {
        if (message.Author.IsBot || message.GuildId is null || string.IsNullOrEmpty(message.Content)
            || !message.Content.Contains("/personen/", StringComparison.OrdinalIgnoreCase)
            && !message.Content.Contains("/fraktionen/", StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.CompletedTask;
        }
        _ = Task.Run(() => ProcessAsync(message));
        return ValueTask.CompletedTask;
    }

    private async Task ProcessAsync(Message message)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var settings = await services.GetRequiredService<IDiscordBotSettingsService>().GetAsync(timeout.Token);
            if (!settings.Enabled || !settings.LinkPreview || !settings.MayShareIn(message.ChannelId))
            {
                return;
            }
            var records = services.GetRequiredService<BotRecords>();
            var link = DiscordLinkParser.Find(message.Content, await records.BaseUrlAsync(timeout.Token));
            if (link is null || !rateLimiter.TryEnter(message.Author.Id))
            {
                return;
            }
            var context = new BotContext(message.Author.Id, message.Author.GlobalName ?? message.Author.Username, message.GuildId, message.ChannelId);
            var (actor, _) = await services.GetRequiredService<IDiscordActorResolver>().ResolveAsync(context, timeout.Token);
            // strangers get nothing, not even a refusal
            if (actor is null)
            {
                return;
            }
            using var ambient = CurrentUserService.ActAs(actor.Principal);
            var call = new BotCall
            {
                Context = context, Actor = actor, Shared = true, Ct = timeout.Token,
                Options = new BotOptions((IReadOnlyList<ApplicationCommandInteractionDataOption>?)null),
            };
            var reply = await services.GetRequiredService<DiscordBotRouter>().CardAsync(link.Value.Type, link.Value.Id, call);
            if (reply.IsError)
            {
                return;
            }
            DiscordCards.FitText(reply.Components);
            var components = reply.Components.Append(new ActionRowProperties(
                [DiscordCards.Button(BotIds.Make("rm", message.Author.Id.ToString()), "Vorschau entfernen", "🗑️")])).ToList();
            var streams = reply.Files.Select(f => new MemoryStream(f.Content)).ToList();
            try
            {
                await message.ReplyAsync(new ReplyMessageProperties
                {
                    Components = components,
                    Flags = MessageFlags.IsComponentsV2,
                    AllowedMentions = AllowedMentionsProperties.None,
                    Attachments = reply.Files.Select((f, i) => new AttachmentProperties(f.Name, streams[i])).ToList(),
                }, cancellationToken: timeout.Token);
            }
            finally
            {
                foreach (var stream in streams)
                {
                    await stream.DisposeAsync();
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("Discord link preview failed ({Error}).", ex.GetType().Name);
        }
    }
}

/// <summary>Marks the gateway as gone for the settings page.</summary>
public sealed class DiscordDisconnectHandler(DiscordBotStatus status) : IDisconnectGatewayHandler
{
    public ValueTask HandleAsync(DisconnectEventArgs arg)
    {
        status.Connected = false;
        return ValueTask.CompletedTask;
    }
}
