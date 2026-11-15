using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Discord.Bot;

namespace NOOSE_Website.Services;

/// <summary>Server and channel of a webhook; a thread id in its URL replaces the channel.</summary>
public sealed record WebhookTarget(ulong GuildId, ulong ChannelId);

/// <summary>Why a post goes through the bot or stays a webhook.</summary>
public enum BotRouteState
{
    /// <summary>The bot posts into the channel.</summary>
    Bot,

    /// <summary>No webhook is set for the category.</summary>
    NoWebhook,

    /// <summary>No bot token, or the bot is switched off.</summary>
    BotOff,

    /// <summary>The webhook belongs to another server, or no server is set.</summary>
    OtherServer,

    /// <summary>The bot failed here a moment ago; the webhook posts for now.</summary>
    Failing,

    /// <summary>The webhook's channel could not be looked up.</summary>
    Unknown,
}

/// <summary>The decision for one webhook; the channel is set only when the bot posts.</summary>
public sealed record BotRoute(BotRouteState State, ulong? ChannelId);

/// <summary>The pure rules behind <see cref="IDiscordBotRoutes"/>.</summary>
public static class DiscordBotRouting
{
    /// <summary>Decides by priority: bot off, foreign server, unresolved webhook, recent failure.</summary>
    public static BotRoute Decide(
        bool botConfigured, bool botEnabled, ulong? guildId, WebhookTarget? target,
        DateTimeOffset? failingUntil, DateTimeOffset now)
    {
        if (!botConfigured || !botEnabled)
        {
            return new BotRoute(BotRouteState.BotOff, null);
        }
        if (guildId is null)
        {
            return new BotRoute(BotRouteState.OtherServer, null);
        }
        if (target is null)
        {
            return new BotRoute(BotRouteState.Unknown, null);
        }
        if (target.GuildId != guildId)
        {
            return new BotRoute(BotRouteState.OtherServer, null);
        }
        return failingUntil > now
            ? new BotRoute(BotRouteState.Failing, null)
            : new BotRoute(BotRouteState.Bot, target.ChannelId);
    }

    /// <summary>The <c>thread_id</c> of a webhook URL, or null.</summary>
    public static ulong? ThreadId(string webhookUrl)
    {
        var q = webhookUrl.IndexOf('?');
        if (q < 0)
        {
            return null;
        }
        var query = webhookUrl[(q + 1)..];
        var fragment = query.IndexOf('#');
        if (fragment >= 0)
        {
            query = query[..fragment];
        }
        foreach (var pair in query.Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "thread_id")
            {
                return DiscordBotSettingsService.ParseId(pair[(eq + 1)..]);
            }
        }
        return null;
    }
}

/// <summary>Tells where the bot may post, derived from each webhook URL.</summary>
public interface IDiscordBotRoutes
{
    /// <summary>Whether the bot posts for this webhook; a failed lookup never throws.</summary>
    Task<BotRoute> ForWebhookAsync(string? webhookUrl, CancellationToken cancellationToken = default);

    /// <summary>Sends this webhook's posts straight to the webhook for ten minutes.</summary>
    void MarkFailing(string webhookUrl);

    /// <summary>Channel of the alarm card, or null when the webhook posts.</summary>
    Task<ulong?> AlarmChannelAsync(CancellationToken cancellationToken = default);

    /// <summary>Channel of the threat board, or null when the webhook posts.</summary>
    Task<ulong?> FactionBoardChannelAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiscordBotRoutes" />
public sealed class DiscordBotRoutes(
    IHttpClientFactory httpFactory,
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    IServiceScopeFactory scopeFactory,
    TimeProvider time) : IDiscordBotRoutes
{
    private const string GuildCacheKey = "discord-bot-route-guild";
    private const string TargetCachePrefix = "discord-bot-route-target:";
    private const string FailingCachePrefix = "discord-bot-route-failing:";
    private static readonly TimeSpan GuildCacheFor = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TargetCacheFor = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedLookupCacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailingFor = TimeSpan.FromMinutes(10);

    public async Task<BotRoute> ForWebhookAsync(string? webhookUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return new BotRoute(BotRouteState.NoWebhook, null);
        }
        var (configured, settings) = await BotAsync(cancellationToken);
        var on = configured && settings.Enabled;
        // skip while off
        var guildId = on ? await GuildIdAsync(cancellationToken) : null;
        var target = guildId is null ? null : await TargetAsync(webhookUrl, cancellationToken);
        DateTimeOffset? failingUntil = on && cache.TryGetValue(FailingCachePrefix + Hash(webhookUrl), out DateTimeOffset until)
            ? until
            : null;
        return DiscordBotRouting.Decide(configured, settings.Enabled, guildId, target, failingUntil, time.GetUtcNow());
    }

    public void MarkFailing(string webhookUrl)
    {
        if (string.IsNullOrWhiteSpace(webhookUrl))
        {
            return;
        }
        cache.Set(FailingCachePrefix + Hash(webhookUrl), time.GetUtcNow() + FailingFor, FailingFor);
    }

    public async Task<ulong?> AlarmChannelAsync(CancellationToken cancellationToken = default)
    {
        var (configured, settings) = await BotAsync(cancellationToken);
        if (!configured || !settings.Enabled)
        {
            return null;
        }
        return settings.AlarmChannelId ?? await WebhookChannelAsync(NotificationType.EmergencyAlarm, cancellationToken);
    }

    public async Task<ulong?> FactionBoardChannelAsync(CancellationToken cancellationToken = default)
    {
        var (configured, settings) = await BotAsync(cancellationToken);
        if (!configured || !settings.Enabled)
        {
            return null;
        }
        return settings.FactionChannelId ?? await WebhookChannelAsync(NotificationType.FactionThreatBoard, cancellationToken);
    }

    /// <summary>Token check and admin settings, read in a short scope.</summary>
    private async Task<(bool Configured, DiscordBotSettings Settings)> BotAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        if (!scope.ServiceProvider.GetRequiredService<IDiscordBotClient>().IsConfigured)
        {
            return (false, DiscordBotSettings.Off);
        }
        var settings = await scope.ServiceProvider.GetRequiredService<IDiscordBotSettingsService>().GetAsync(cancellationToken);
        return (true, settings);
    }

    /// <summary>Channel of a category's webhook, if webhooks are on and the bot may post.</summary>
    private async Task<ulong?> WebhookChannelAsync(NotificationType type, CancellationToken cancellationToken)
    {
        DiscordWebhookConfig config;
        using (var scope = scopeFactory.CreateScope())
        {
            config = await scope.ServiceProvider.GetRequiredService<IDiscordWebhookService>().GetConfigAsync(cancellationToken);
        }
        if (!config.Enabled)
        {
            return null;
        }
        var route = await ForWebhookAsync(config.Webhooks.GetValueOrDefault(type), cancellationToken);
        return route.State == BotRouteState.Bot ? route.ChannelId : null;
    }

    private async Task<ulong?> GuildIdAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(GuildCacheKey, out ulong? cached))
        {
            return cached;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var value = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordGuildId)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        var id = DiscordBotSettingsService.ParseId(value);
        cache.Set(GuildCacheKey, id, GuildCacheFor);
        return id;
    }

    /// <summary>Cached by URL hash; a failed lookup is cached shorter.</summary>
    private async Task<WebhookTarget?> TargetAsync(string webhookUrl, CancellationToken cancellationToken)
    {
        var key = TargetCachePrefix + Hash(webhookUrl);
        if (cache.TryGetValue(key, out WebhookTarget? cached))
        {
            return cached;
        }
        var target = await LookUpAsync(webhookUrl, cancellationToken);
        cache.Set(key, target, target is null ? FailedLookupCacheFor : TargetCacheFor);
        return target;
    }

    /// <summary>Asks Discord for the webhook's server and channel, never reading the token.</summary>
    private async Task<WebhookTarget?> LookUpAsync(string webhookUrl, CancellationToken cancellationToken)
    {
        try
        {
            var query = webhookUrl.IndexOf('?');
            var path = query < 0 ? webhookUrl : webhookUrl[..query];
            using var response = await httpFactory.CreateClient("discord").GetAsync(path, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var json = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken);
            var guild = ReadId(json.RootElement, "guild_id");
            var channel = DiscordBotRouting.ThreadId(webhookUrl) ?? ReadId(json.RootElement, "channel_id");
            return guild is { } g && channel is { } c ? new WebhookTarget(g, c) : null;
        }
        catch when (!cancellationToken.IsCancellationRequested)
        {
            /* best effort */
            return null;
        }
    }

    private static ulong? ReadId(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? DiscordBotSettingsService.ParseId(value.GetString())
            : null;

    /// <summary>Cache keys carry the URL's hash, never the secret URL.</summary>
    private static string Hash(string webhookUrl)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(webhookUrl)));
}
