using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFactionThreatBoardService" />
public sealed class FactionThreatBoardService(
    IDbContextFactory<AppDbContext> dbFactory, IDiscordWebhookService discord, ILogger<FactionThreatBoardService> logger,
    TimeProvider? clock = null)
    : IFactionThreatBoardService
{
    // the worker and the settings button must never both post a first board
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>How long a removal that keeps failing holds the board back before the pass gives up on it.</summary>
    public static readonly TimeSpan RetireGiveUpAfter = TimeSpan.FromHours(1);

    // static: every pass gets a fresh service; guarded by the gate
    private static (string Ref, DateTimeOffset Since)? _retireFailing;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public async Task<ThreatBoardOutcome> RefreshAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write before rank: the pass stores the board's state
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        return await RunAsync(force: true, cancellationToken);
    }

    public async Task<ThreatBoardOutcome> RunAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            return await RunLockedAsync(force, cancellationToken);
        }
        finally
        {
            Gate.Release();
        }
    }

    private async Task<ThreatBoardOutcome> RunLockedAsync(bool force, CancellationToken cancellationToken)
    {
        var config = await discord.GetConfigAsync(cancellationToken);
        var url = config.Enabled && config.Webhooks.GetValueOrDefault(NotificationType.FactionThreatBoard) is { Length: > 0 } u
            ? u
            : null;

        var (storedRef, storedFingerprint) = await LoadStateAsync(cancellationToken);
        var stored = FactionThreatBoard.ParseRef(storedRef);

        // a board nobody updates any more would go on naming a faction classified since, so it goes first
        if (stored is not null && stored.WebhookUrl != url)
        {
            if (await RetireAsync(stored, url, cancellationToken) == ThreatBoardRemoval.Failed && !GiveUpRetire(storedRef!, stored))
            {
                return ThreatBoardOutcome.Failed;
            }
            _retireFailing = null;
            await StoreAsync(SystemSettingKeys.DiscordThreatBoardMessage, string.Empty);
            stored = null;
        }
        if (url is null)
        {
            return ThreatBoardOutcome.Off;
        }

        var threshold = config.ThreatBoardThreshold;
        var entries = FactionThreatBoard.Order(await LoadEntriesAsync(threshold, cancellationToken));
        var fingerprint = FactionThreatBoard.Fingerprint(entries, threshold, config.SiteBaseUrl);
        var messageId = stored?.MessageId;
        if (storedFingerprint == fingerprint && messageId is not null && !force)
        {
            return ThreatBoardOutcome.Unchanged;
        }

        // the stamp is "as of": a forced pass renews it, so a board nobody updates any more shows its age
        var embed = FactionThreatBoard.Render(entries, threshold, config.SiteBaseUrl, _clock.GetUtcNow().UtcDateTime);

        var edit = ThreatBoardEdit.Missing;
        if (messageId is not null)
        {
            edit = await discord.EditThreatBoardAsync(url, messageId, embed, cancellationToken);
            if (edit == ThreatBoardEdit.Edited)
            {
                await StoreAsync(SystemSettingKeys.DiscordThreatBoardState, fingerprint);
                return ThreatBoardOutcome.Edited;
            }
            if (edit == ThreatBoardEdit.Failed)
            {
                return ThreatBoardOutcome.Failed;
            }
            // gone: forget it, or a failed repost below would wait for the next forced pass. An archived one stays
            // known until the repost has reopened its thread and it can be deleted
            if (edit == ThreatBoardEdit.Missing)
            {
                await StoreAsync(SystemSettingKeys.DiscordThreatBoardMessage, string.Empty);
            }
        }

        // not cancellable: a post Discord took but we dropped would leave a second, orphaned board
        var posted = await discord.PostThreatBoardAsync(url, embed, CancellationToken.None);
        if (posted is null)
        {
            return ThreatBoardOutcome.Failed;
        }
        await StoreAsync(SystemSettingKeys.DiscordThreatBoardMessage, FactionThreatBoard.MessageRef(posted, url));
        await StoreAsync(SystemSettingKeys.DiscordThreatBoardState, fingerprint);
        if (edit == ThreatBoardEdit.Archived)
        {
            // the post reopened the thread; the frozen copy would otherwise go on showing an old list
            await RetireAsync(new ThreatBoardMessage(messageId!, url), url, CancellationToken.None);
        }
        return ThreatBoardOutcome.Posted;
    }

    private async Task<ThreatBoardRemoval> RetireAsync(ThreatBoardMessage old, string? currentUrl, CancellationToken cancellationToken)
    {
        var removal = await discord.DeleteThreatBoardAsync(old.WebhookUrl, old.MessageId, cancellationToken);
        // a rotated token: the same webhook still owns the message under its new URL, in its old thread
        if (removal == ThreatBoardRemoval.Unreachable && currentUrl is not null && currentUrl != old.WebhookUrl
            && FactionThreatBoard.WebhookId(currentUrl) == FactionThreatBoard.WebhookId(old.WebhookUrl))
        {
            removal = await discord.DeleteThreatBoardAsync(
                FactionThreatBoard.WithQueryOf(currentUrl, old.WebhookUrl), old.MessageId, cancellationToken);
        }
        if (removal == ThreatBoardRemoval.Unreachable)
        {
            logger.LogWarning("The previous threat board message {MessageId} could not be removed; delete it in Discord.", old.MessageId);
        }
        return removal;
    }

    // a removal that never gets through must not hold the board back for good
    private bool GiveUpRetire(string storedRef, ThreatBoardMessage old)
    {
        var now = _clock.GetUtcNow();
        if (_retireFailing?.Ref != storedRef)
        {
            _retireFailing = (storedRef, now);
        }
        if (now - _retireFailing.Value.Since < RetireGiveUpAfter)
        {
            return false;
        }
        logger.LogWarning("Removing the previous threat board message {MessageId} kept failing; giving up, delete it in Discord.",
            old.MessageId);
        return true;
    }

    /// <summary>Active, unclassified factions above the threshold; the channel's readers are unknown to the app.</summary>
    private async Task<List<ThreatBoardEntry>> LoadEntriesAsync(int threshold, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Factions
            .OnlyActive()
            .OnlyUnrestricted()
            .Where(f => !f.IsStateFaction && f.ThreatScore != null && f.ThreatScore > threshold)
            .Select(f => new ThreatBoardEntry(f.Id, f.Name, f.ThreatScore!.Value))
            .ToListAsync(cancellationToken);
    }

    private async Task<(string? MessageRef, string? Fingerprint)> LoadStateAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordThreatBoardMessage || s.Key == SystemSettingKeys.DiscordThreatBoardState)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        return (rows.GetValueOrDefault(SystemSettingKeys.DiscordThreatBoardMessage),
            rows.GetValueOrDefault(SystemSettingKeys.DiscordThreatBoardState));
    }

    // ExecuteUpdate keeps every board change out of the change log; only the very first row is a tracked insert.
    // Existence is asked, not the update count: MySQL may count only changed rows, and rewriting the same hash is normal
    private async Task StoreAsync(string key, string value)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        if (await db.SystemSettings.AnyAsync(s => s.Key == key, CancellationToken.None))
        {
            await db.SystemSettings
                .Where(s => s.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, value), CancellationToken.None);
            return;
        }
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        await db.SaveChangesAsync(CancellationToken.None);
    }
}
