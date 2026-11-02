using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IChannelShareService" />
public sealed class ChannelShareService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordWebhookService discord,
    IAccessLogService accessLog,
    IMemoryCache cache,
    ChannelShareQuota quota) : IChannelShareService
{
    private const string CacheKey = "DiscordShareChannels";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    private sealed record Snapshot(bool Enabled, string BaseUrl, IReadOnlyList<StoredShareChannel> Channels);

    private sealed record Shareable(string Name, string CaseNumber);

    public async Task<IReadOnlyList<ShareChannel>> GetChannelsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // button feed, refuses silently
        if (!actor.MayShareToChannel())
        {
            return [];
        }
        if (!cache.TryGetValue(CacheKey, out Snapshot? snapshot) || snapshot is null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            snapshot = await ReadAsync(db, cancellationToken);
            cache.Set(CacheKey, snapshot, CacheDuration);
        }
        return snapshot.Enabled ? snapshot.Channels.Select(c => new ShareChannel(c.Id, c.Name)).ToList() : [];
    }

    public async Task<IReadOnlyList<ShareChannelSetting>> GetSettingsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireInternalAgent(actor);
        Permission.RequireClassifiedRead(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var stored = ChannelShareRules.Parse(await StoredValueAsync(db, cancellationToken));
        return stored.Select(c => new ShareChannelSetting(c.Id, c.Name, ChannelShareRules.Mask(c.Url))).ToList();
    }

    public async Task SaveSettingsAsync(IReadOnlyList<ShareChannelInput> channels, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireShareChannelAdministration(actor);
        ArgumentNullException.ThrowIfNull(channels);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var current = await StoredValueAsync(db, cancellationToken);
        var exists = current is not null
            || await db.SystemSettings.AnyAsync(s => s.Key == SystemSettingKeys.DiscordShareChannels, cancellationToken);
        var stored = ChannelShareRules.Parse(current);
        var (next, replaced) = ChannelShareRules.Merge(stored, channels, () => Guid.NewGuid().ToString());
        var json = ChannelShareRules.Serialize(next);
        if (json == ChannelShareRules.Serialize(stored))
        {
            return;
        }

        // list and audit row together
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);
        if (exists)
        {
            // keeps webhooks out of audit
            var now = DateTime.UtcNow;
            var actorId = actor.GetAgentId();
            await db.SystemSettings
                .Where(s => s.Key == SystemSettingKeys.DiscordShareChannels)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(s => s.Value, json)
                    .SetProperty(s => s.ModifiedAt, now)
                    .SetProperty(s => s.ModifiedById, actorId), cancellationToken);
        }
        else
        {
            // insert audits without values
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.DiscordShareChannels, Value = json });
        }

        // names only, never a webhook
        var changes = ManualAudit.Change("Kanäle", Names(stored), Names(next));
        if (replaced.Count > 0)
        {
            changes["Webhook ersetzt"] = [null, string.Join(", ", replaced)];
        }
        db.AuditLogs.Add(ManualAudit.Row(ChannelShareRules.AuditType, SystemSettingKeys.DiscordShareChannels,
            AuditAction.Modified, actor, changes));
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task<string> ShareAsync(ChannelShareRequest request, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireChannelShare(actor);
        ArgumentNullException.ThrowIfNull(request);
        var note = ChannelShareRules.CleanNote(request.Note);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // fresh read, not cached
        var snapshot = await ReadAsync(db, cancellationToken);
        if (!snapshot.Enabled)
        {
            throw new InvalidOperationException("Discord ist ausgeschaltet, Teilen geht gerade nicht.");
        }
        var channel = snapshot.Channels.FirstOrDefault(c => c.Id == request.ChannelId)
            ?? throw new InvalidOperationException("Diesen Kanal gibt es nicht mehr. Lade die Seite neu.");
        var record = await LoadShareableAsync(db, request.RecordType, request.RecordId, ViewerScope.From(actor),
                cancellationToken)
            ?? throw new InvalidOperationException(ChannelShareRules.Refused);

        quota.Take(actor.GetAgentId()!);
        var content = ChannelShareRules.Compose(request.RecordType, record.Name, record.CaseNumber, note,
            ChannelShareRules.Link(snapshot.BaseUrl, request.RecordType, request.RecordId));
        // logged before it leaves
        await accessLog.LogViewAsync(ChannelShareRules.LogType,
            ChannelShareRules.LogEntry(request.RecordType, request.RecordId), cancellationToken);
        if (!await discord.PushToShareChannelAsync(channel.Url, content, cancellationToken))
        {
            throw new InvalidOperationException("Discord hat den Beitrag nicht angenommen. Versuch es später noch einmal.");
        }
        return channel.Name;
    }

    /// <summary>Name and case number of a record without any secrecy level that the actor sees; null otherwise.</summary>
    private static async Task<Shareable?> LoadShareableAsync(AppDbContext db, string recordType, string recordId,
        ViewerScope scope, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(recordId) || !ChannelShareRules.ShareableTypes.Contains(recordType))
        {
            return null;
        }
        var row = recordType switch
        {
            nameof(Person) => await db.People.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Name, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            nameof(Faction) => await db.Factions.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Name, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Name, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            nameof(Party) => await db.Parties.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Name, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            nameof(Case) => await db.Cases.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Title, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            nameof(Operation) => await db.Operations.AsNoTracking().OnlyUnrestricted().Where(x => x.Id == recordId)
                .Select(x => new Shareable(x.Title, x.CaseNumber)).FirstOrDefaultAsync(cancellationToken),
            _ => null,
        };
        // sharer's own sight too
        return row is not null && await Visibility.IsRecordVisibleAsync(db, recordType, recordId, scope, cancellationToken)
            ? row
            : null;
    }

    private static async Task<Snapshot> ReadAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordEnabled
                || s.Key == SystemSettingKeys.SiteBaseUrl
                || s.Key == SystemSettingKeys.DiscordShareChannels)
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        var enabled = string.Equals(values.GetValueOrDefault(SystemSettingKeys.DiscordEnabled), "true",
            StringComparison.OrdinalIgnoreCase);
        var baseUrl = values.GetValueOrDefault(SystemSettingKeys.SiteBaseUrl) is { } raw && !string.IsNullOrWhiteSpace(raw)
            ? raw.Trim()
            : DiscordWebhookConfig.DefaultBaseUrl;
        return new Snapshot(enabled, baseUrl,
            ChannelShareRules.Parse(values.GetValueOrDefault(SystemSettingKeys.DiscordShareChannels)));
    }

    private static Task<string?> StoredValueAsync(AppDbContext db, CancellationToken cancellationToken)
        => db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordShareChannels)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

    private static string Names(IEnumerable<StoredShareChannel> channels)
    {
        var names = string.Join(", ", channels.Select(c => c.Name));
        return names.Length == 0 ? "—" : names;
    }
}
