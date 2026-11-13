using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services.Discord.Bot;

/// <summary>Admin choices for the Discord bot.</summary>
public sealed record DiscordBotSettings(
    bool Enabled,
    ulong? AlarmChannelId,
    ulong? WantedChannelId,
    IReadOnlyList<ulong> ShareChannelIds,
    bool LinkPreview,
    ulong? CommandsChannelId = null,
    ulong? FactionChannelId = null)
{
    public static readonly DiscordBotSettings Off = new(false, null, null, [], false);

    /// <summary>Empty list means every channel of the server.</summary>
    public bool MayShareIn(ulong? channelId)
        => channelId is { } id && (ShareChannelIds.Count == 0 || ShareChannelIds.Contains(id));
}

/// <summary>Reads and saves the bot settings; reads are cached for a few seconds because every interaction asks.</summary>
public interface IDiscordBotSettingsService
{
    Task<DiscordBotSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DiscordBotSettings settings, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IDiscordBotSettingsService" />
public sealed class DiscordBotSettingsService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : IDiscordBotSettingsService
{
    private const string CacheKey = "discord-bot-settings";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(10);

    private static readonly string[] Keys =
    [
        SystemSettingKeys.DiscordBotEnabled, SystemSettingKeys.DiscordBotAlarmChannel,
        SystemSettingKeys.DiscordBotWantedChannel, SystemSettingKeys.DiscordBotShareChannels,
        SystemSettingKeys.DiscordBotLinkPreview, SystemSettingKeys.DiscordBotCommandsChannel,
        SystemSettingKeys.DiscordBotFactionChannel,
    ];

    public async Task<DiscordBotSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out DiscordBotSettings? cached) && cached is not null)
        {
            return cached;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => Keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var settings = new DiscordBotSettings(
            IsOn(values.GetValueOrDefault(SystemSettingKeys.DiscordBotEnabled)),
            ParseId(values.GetValueOrDefault(SystemSettingKeys.DiscordBotAlarmChannel)),
            ParseId(values.GetValueOrDefault(SystemSettingKeys.DiscordBotWantedChannel)),
            ParseIds(values.GetValueOrDefault(SystemSettingKeys.DiscordBotShareChannels)),
            IsOn(values.GetValueOrDefault(SystemSettingKeys.DiscordBotLinkPreview)),
            ParseId(values.GetValueOrDefault(SystemSettingKeys.DiscordBotCommandsChannel)),
            ParseId(values.GetValueOrDefault(SystemSettingKeys.DiscordBotFactionChannel)));
        cache.Set(CacheKey, settings, CacheFor);
        return settings;
    }

    public async Task SaveAsync(DiscordBotSettings settings, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotEnabled, settings.Enabled ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotAlarmChannel, Format(settings.AlarmChannelId), cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotWantedChannel, Format(settings.WantedChannelId), cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotShareChannels,
            settings.ShareChannelIds.Count == 0 ? null : string.Join(",", settings.ShareChannelIds.Distinct()), cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotLinkPreview, settings.LinkPreview ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotCommandsChannel, Format(settings.CommandsChannelId), cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordBotFactionChannel, Format(settings.FactionChannelId), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>A Discord snowflake, or null for anything else.</summary>
    public static ulong? ParseId(string? value)
        => DiscordWebhookService.IsSnowflake(value?.Trim()) && ulong.TryParse(value!.Trim(), NumberStyles.None,
            CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>Comma, space or line separated snowflakes; anything else is dropped.</summary>
    public static IReadOnlyList<ulong> ParseIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split([',', ' ', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseId)
                .OfType<ulong>()
                .Distinct()
                .ToList();

    private static string? Format(ulong? id) => id?.ToString(CultureInfo.InvariantCulture);

    private static bool IsOn(string? value) => string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (value is null)
        {
            if (row is not null)
            {
                db.SystemSettings.Remove(row);
            }
        }
        else if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
