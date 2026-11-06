using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordDmService" />
public sealed partial class DiscordDmService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordBotClient bot,
    IDiscordWebhookService webhook,
    ILogger<DiscordDmService> logger) : IDiscordDmService
{
    private const string DigestRoute = "/benachrichtigungen";
    private const int MaxInviteLinkChars = 120;

    private static readonly string[] InvitePrefixes = ["https://discord.gg/", "https://discord.com/invite/"];

    // the code, plus an optional query like ?event=...
    [GeneratedRegex(@"\A[A-Za-z0-9_-]+(\?[A-Za-z0-9_=&%.-]*)?\z")]
    private static partial Regex InviteTail();

    public async Task<DiscordDmSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadSettingsAsync(db, cancellationToken);
    }

    public async Task SaveSettingsAsync(bool personal, bool applicants, string? inviteLink, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        var link = inviteLink?.Trim();
        if (!string.IsNullOrEmpty(link) && !IsValidInviteLink(link))
        {
            throw new InvalidOperationException("Der Einladungslink muss mit https://discord.gg/ oder "
                + "https://discord.com/invite/ beginnen und danach nur Buchstaben, Ziffern, - und _ enthalten.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordDmPersonal, personal ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordDmApplicants, applicants ? "true" : "false", cancellationToken);
        // nothing stored for the default, so a later change of it reaches everyone who never set their own
        await SetAsync(db, SystemSettingKeys.DiscordInviteLink,
            string.IsNullOrEmpty(link) || link == DiscordDmSettings.DefaultInviteLink ? null : link, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>An https invite to Discord and nothing else: the link ends up in an href and in a refusal text.</summary>
    public static bool IsValidInviteLink(string? link)
        => !string.IsNullOrWhiteSpace(link) && link.Length <= MaxInviteLinkChars
            && InvitePrefixes.Any(prefix => link.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && InviteTail().IsMatch(link[prefix.Length..]));

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
                // the message carries its own mention
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
        string[] keys = [SystemSettingKeys.DiscordDmPersonal, SystemSettingKeys.DiscordDmApplicants,
            SystemSettingKeys.DiscordInviteLink];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        return new DiscordDmSettings(IsOn(values, SystemSettingKeys.DiscordDmPersonal),
            IsOn(values, SystemSettingKeys.DiscordDmApplicants), bot.IsConfigured, ReadInviteLink(values));
    }

    /// <summary>The stored link if it still passes the check, else the default; a value written around the service never reaches an href.</summary>
    private static string ReadInviteLink(Dictionary<string, string?> values)
        => values.GetValueOrDefault(SystemSettingKeys.DiscordInviteLink)?.Trim() is { Length: > 0 } link && IsValidInviteLink(link)
            ? link
            : DiscordDmSettings.DefaultInviteLink;

    private static bool IsOn(Dictionary<string, string?> values, string key)
        => string.Equals(values.GetValueOrDefault(key), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>A null value removes the row.</summary>
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
