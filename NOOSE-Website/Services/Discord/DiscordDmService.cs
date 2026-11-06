using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordDmService" />
public sealed class DiscordDmService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordBotClient bot,
    IDiscordWebhookService webhook,
    ILogger<DiscordDmService> logger) : IDiscordDmService
{
    private const string DigestRoute = "/benachrichtigungen";

    public async Task<DiscordDmSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadSettingsAsync(db, cancellationToken);
    }

    public async Task SaveSettingsAsync(bool personal, bool applicants, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordDmPersonal, personal, cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordDmApplicants, applicants, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> PersonalActiveAsync(CancellationToken cancellationToken = default)
        => await GetSettingsAsync(cancellationToken) is { Personal: true, BotConfigured: true };

    public async Task<bool> ApplicantsActiveAsync(CancellationToken cancellationToken = default)
        => await GetSettingsAsync(cancellationToken) is { Applicants: true, BotConfigured: true };

    public async Task DeliverAsync(DiscordDmJob job, CancellationToken cancellationToken = default)
    {
        // never pinged anyway
        if (job.AgentId == DemoIdentity.AgentId || !DiscordWebhookService.IsSnowflake(job.DiscordId))
        {
            logger.LogDebug("Discord direct message for agent {AgentId} skipped: no Discord recipient.", job.AgentId);
            return;
        }

        if (string.IsNullOrWhiteSpace(job.Content))
        {
            logger.LogWarning("Discord direct message for agent {AgentId} skipped: no text.", job.AgentId);
            return;
        }

        try
        {
            await bot.SendDirectMessageAsync(job.DiscordId, job.Content, cancellationToken);
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            // never the text or the Discord id
            logger.LogWarning("Discord direct message for agent {AgentId} failed ({Error}).", job.AgentId, Describe(ex));
            if (IsClosed(ex))
            {
                await StampAsync(job.AgentId, DateTime.UtcNow, cancellationToken);
            }
            await FallBackAsync(job, cancellationToken);
            return;
        }

        await StampAsync(job.AgentId, null, cancellationToken);
    }

    /// <summary>The user shut direct messages (50007), or Discord refuses the bot outright (403).</summary>
    private static bool IsClosed(Exception ex)
        => ex is DiscordBotException { Code: DiscordBotException.CannotSendToUser } or DiscordBotException { Status: 403 };

    /// <summary>Agents get the message as the channel post they got before; applicants have the portal bell.</summary>
    private async Task FallBackAsync(DiscordDmJob job, CancellationToken cancellationToken)
    {
        // role pings, no fallback
        if (job.FallbackType is not { } type || !(DiscordPings.IsPersonal(type) || type == NotificationType.PingDigest))
        {
            return;
        }

        try
        {
            if (type != NotificationType.PingDigest)
            {
                await webhook.PushAsync(type, job.FallbackHref, [job.AgentId], null, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(job.FallbackContent))
            {
                // the line carries its own mention
                await webhook.PushDigestAsync(job.FallbackContent, [job.DiscordId], job.FallbackHref ?? DigestRoute,
                    cancellationToken);
            }
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            logger.LogWarning("Discord channel fallback for agent {AgentId} failed ({Error}).", job.AgentId, Describe(ex));
        }
    }

    // ExecuteUpdate only: UserManager rewrites the whole row from a possibly stale copy
    private async Task StampAsync(string agentId, DateTime? blockedAt, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var row = db.Users.Where(u => u.Id == agentId);
            if (blockedAt is null)
            {
                // nothing to write while it is clear
                await row.Where(u => u.DiscordDmBlockedAt != null)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordDmBlockedAt, (DateTime?)null), cancellationToken);
            }
            else
            {
                await row.ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordDmBlockedAt, blockedAt), cancellationToken);
            }
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            logger.LogWarning("Discord direct message stamp for agent {AgentId} failed ({Error}).", agentId, Describe(ex));
        }
    }

    /// <summary>Status and code of a Discord refusal, else the exception type; never a message that could echo text.</summary>
    private static string Describe(Exception ex)
        => ex is DiscordBotException discord ? $"{discord.Status}/{discord.Code?.ToString() ?? "-"}" : ex.GetType().Name;

    /// <summary>Own token only; a timeout also surfaces as a cancellation but is a failure.</summary>
    private static bool IsOwnCancellation(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private async Task<DiscordDmSettings> ReadSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        string[] keys = [SystemSettingKeys.DiscordDmPersonal, SystemSettingKeys.DiscordDmApplicants];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        return new DiscordDmSettings(IsOn(values, SystemSettingKeys.DiscordDmPersonal),
            IsOn(values, SystemSettingKeys.DiscordDmApplicants), bot.IsConfigured);
    }

    private static bool IsOn(Dictionary<string, string?> values, string key)
        => string.Equals(values.GetValueOrDefault(key), "true", StringComparison.OrdinalIgnoreCase);

    private static async Task SetAsync(AppDbContext db, string key, bool on, CancellationToken cancellationToken)
    {
        var value = on ? "true" : "false";
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
