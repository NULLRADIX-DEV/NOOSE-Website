using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IDiscordRoleSyncService" />
public sealed class DiscordRoleSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    IDiscordBotClient bot,
    IDiscordRoleSyncQueue queue,
    ILogger<DiscordRoleSyncService> logger) : IDiscordRoleSyncService
{
    // width of the error column
    private const int MaxErrorChars = 200;
    private const int MaxReasonChars = 100;
    private const int MinIdChars = 17;
    private const int MaxIdChars = 20;
    private const string ReasonPrefix = "FIB-Website: ";
    private const string DefaultReason = "Abgleich";

    private static readonly JsonSerializerOptions MappingJson = new() { PropertyNameCaseInsensitive = true };

    public async Task<DiscordRoleSyncSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await ReadSettingsAsync(db, cancellationToken);
    }

    public async Task SaveSettingsAsync(DiscordRoleSyncSettings settings, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        var guildId = Trim(settings.GuildId);
        if (guildId is not null && !IsSnowflake(guildId))
        {
            throw new InvalidOperationException("Die Server-Id muss aus 17 bis 20 Ziffern bestehen.");
        }

        var mapping = Normalize(settings.Mapping);
        if (mapping.AllManaged.FirstOrDefault(id => !IsSnowflake(id)) is { } badId)
        {
            throw new InvalidOperationException(
                $"„{Shorten(badId, 30)}“ ist keine gültige Rollen-Id (17 bis 20 Ziffern).");
        }
        if (DiscordRoleRules.Validate(mapping) is { } problem)
        {
            throw new InvalidOperationException(problem);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordRoleSyncEnabled, settings.Enabled ? "true" : "false", cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordGuildId, guildId, cancellationToken);
        await SetAsync(db, SystemSettingKeys.DiscordRoleMapping, JsonSerializer.Serialize(mapping, MappingJson), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<DiscordGuildRoles?> GetGuildRolesAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);

        var settings = await GetSettingsAsync(cancellationToken);
        if (!bot.IsConfigured || settings.GuildId is null)
        {
            return null;
        }
        return await TryGuildRolesAsync(settings.GuildId, cancellationToken);
    }

    public async Task<IReadOnlyList<DiscordRolePreviewRow>> PreviewAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ReadSettingsAsync(db, cancellationToken);
        if (!bot.IsConfigured || settings.GuildId is null)
        {
            throw new InvalidOperationException("Für den Probelauf fehlen der Bot-Token oder die Server-Id.");
        }

        var guildRoles = await TryGuildRolesAsync(settings.GuildId, cancellationToken);
        var rows = new List<DiscordRolePreviewRow>();
        foreach (var agent in await LoadAgentsAsync(db, cancellationToken))
        {
            if (DiscordRoleRules.Target(agent, settings.Mapping) is not { } target)
            {
                continue;
            }
            if (await PreviewRowAsync(settings.GuildId, agent, target, guildRoles, cancellationToken) is { } row)
            {
                rows.Add(row);
            }
        }
        return rows;
    }

    public async Task<DiscordRoleSyncStatus> GetStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireClassifiedRead(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ReadSettingsAsync(db, cancellationToken);
        var lastRun = await db.SystemSettings.AsNoTracking()
            .Where(s => s.Key == SystemSettingKeys.DiscordRoleSyncLastRun)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);
        var failed = await db.Users.AsNoTracking()
            .Where(u => u.DiscordRolesSyncError != null)
            .OrderBy(u => u.Codename).ThenBy(u => u.Id)
            .Select(u => new { u.Id, u.Codename, u.DiscordRolesSyncError, u.DiscordRolesSyncAt })
            .ToListAsync(cancellationToken);

        var failures = failed
            .Select(u => new DiscordRoleSyncFailure(u.Id, u.Codename, u.DiscordRolesSyncError!, AsUtc(u.DiscordRolesSyncAt)))
            .ToList();
        return new DiscordRoleSyncStatus(bot.IsConfigured, settings.Enabled, ParseUtc(lastRun), failures);
    }

    public Task RunNowAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireAdmin(actor);

        queue.EnqueueAll(DiscordRoleSyncReason.Manual);
        return Task.CompletedTask;
    }

    public async Task SyncAgentAsync(string agentId, string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SyncFreshAsync(db, agentId, Reason(reason), cancellationToken);
    }

    public async Task SyncDeletedAsync(string discordId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var settings = await ReadSettingsAsync(db, cancellationToken);
        if (!IsActive(settings) || string.IsNullOrWhiteSpace(discordId))
        {
            return;
        }

        // delete may have rolled back
        var live = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.DiscordId == discordId, cancellationToken);
        if (live is not null)
        {
            await SyncOneAsync(db, live, settings, Reason(DiscordRoleSyncReason.AccountDeleted), cancellationToken);
            return;
        }

        try
        {
            var target = DiscordRoleRules.DeletedTarget(settings.Mapping);
            await SyncMemberAsync(settings.GuildId!, discordId, target, Reason(DiscordRoleSyncReason.AccountDeleted), cancellationToken);
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            // no row left to hold the error
            logger.LogWarning("Discord role sync for a deleted account failed: {Error}", ErrorText(ex));
        }
    }

    public async Task SyncAllAsync(string reason, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!IsActive(await ReadSettingsAsync(db, cancellationToken)))
        {
            return;
        }

        var agentIds = await db.Users.AsNoTracking().Where(u => u.DiscordId != "").OrderBy(u => u.Id)
            .Select(u => u.Id).ToListAsync(cancellationToken);
        var why = Reason(reason);
        foreach (var agentId in agentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // switched off or token gone mid-run: stop, and leave the run unstamped
            if (!await SyncFreshAsync(db, agentId, why, cancellationToken))
            {
                return;
            }
        }

        await StampLastRunAsync(db, cancellationToken);
    }

    private bool IsActive(DiscordRoleSyncSettings settings) => settings.Enabled && bot.IsConfigured && settings.GuildId is not null;

    /// <summary>Reads switch, mapping and row anew; false when the sync is no longer active.</summary>
    private async Task<bool> SyncFreshAsync(AppDbContext db, string agentId, string reason, CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(db, cancellationToken);
        if (!IsActive(settings))
        {
            return false;
        }

        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == agentId, cancellationToken);
        if (agent is not null)
        {
            await SyncOneAsync(db, agent, settings, reason, cancellationToken);
        }
        return true;
    }

    // worker state, no actor: ExecuteUpdate past the audit, only the first row is a tracked insert
    private static async Task StampLastRunAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        var key = SystemSettingKeys.DiscordRoleSyncLastRun;
        if (await db.SystemSettings.AnyAsync(s => s.Key == key, cancellationToken))
        {
            await db.SystemSettings.Where(s => s.Key == key)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Value, now), cancellationToken);
            return;
        }
        db.SystemSettings.Add(new SystemSetting { Key = key, Value = now });
        await db.SaveChangesAsync(cancellationToken);
    }

    private static Task<List<Agent>> LoadAgentsAsync(AppDbContext db, CancellationToken cancellationToken)
        => db.Users.AsNoTracking().Where(u => u.DiscordId != "").OrderBy(u => u.Id).ToListAsync(cancellationToken);

    /// <summary>One agent; the outcome goes onto the row, only cancellation escapes.</summary>
    private async Task SyncOneAsync(AppDbContext db, Agent agent, DiscordRoleSyncSettings settings, string reason,
        CancellationToken cancellationToken)
    {
        if (DiscordRoleRules.Target(agent, settings.Mapping) is not { } target)
        {
            // else a stale error stays listed
            if (agent.DiscordRolesSyncError is not null)
            {
                await db.Users.Where(u => u.Id == agent.Id)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordRolesSyncError, (string?)null), cancellationToken);
            }
            return;
        }

        string? error;
        try
        {
            error = await SyncMemberAsync(settings.GuildId!, agent.DiscordId, target, reason, cancellationToken);
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            error = ErrorText(ex);
            logger.LogWarning("Discord role sync for agent {AgentId} failed: {Error}", agent.Id, error);
        }
        await WriteResultAsync(db, agent.Id, error, cancellationToken);
    }

    /// <summary>Null once in line, <see cref="DiscordRoleRules.NotOnServer"/> if absent; throws the first failure after trying every role.</summary>
    private async Task<string?> SyncMemberAsync(string guildId, string discordId, DiscordRoleTarget target, string reason,
        CancellationToken cancellationToken)
    {
        var current = await bot.GetMemberRolesAsync(guildId, discordId, cancellationToken);
        if (current is null)
        {
            return DiscordRoleRules.NotOnServer;
        }

        var diff = DiscordRoleRules.Diff(target, current);
        Exception? first = null;
        // removals first, they close access
        foreach (var role in diff.Remove)
        {
            first = await AttemptAsync(() => bot.RemoveRoleAsync(guildId, discordId, role, reason, cancellationToken), first,
                cancellationToken);
        }
        foreach (var role in diff.Add)
        {
            first = await AttemptAsync(() => bot.AddRoleAsync(guildId, discordId, role, reason, cancellationToken), first,
                cancellationToken);
        }

        if (first is not null)
        {
            throw first;
        }
        return null;
    }

    /// <summary>One role call; keeps the first role-local failure, any other failure ends the agent.</summary>
    private static async Task<Exception?> AttemptAsync(Func<Task> call, Exception? first, CancellationToken cancellationToken)
    {
        try
        {
            await call();
            return first;
        }
        catch (Exception ex) when (IsRoleLocal(ex))
        {
            return first ?? ex;
        }
        catch (Exception ex) when (first is not null && !IsOwnCancellation(ex, cancellationToken))
        {
            // would hit every call alike
            throw first;
        }
    }

    /// <summary>Only this role failed (refused, unknown, bad id); the rest would fail every call.</summary>
    private static bool IsRoleLocal(Exception ex)
        => ex is ArgumentException or DiscordBotException { Status: >= 400 and < 500 and not (401 or 429) };

    private async Task<DiscordRolePreviewRow?> PreviewRowAsync(string guildId, Agent agent, DiscordRoleTarget target,
        DiscordGuildRoles? guildRoles, CancellationToken cancellationToken)
    {
        try
        {
            var current = await bot.GetMemberRolesAsync(guildId, agent.DiscordId, cancellationToken);
            if (current is null)
            {
                return new DiscordRolePreviewRow(agent.Id, agent.Codename, [], [], DiscordRoleRules.NotOnServer);
            }

            var diff = DiscordRoleRules.Diff(target, current);
            if (diff.IsEmpty)
            {
                return null;
            }
            return new DiscordRolePreviewRow(agent.Id, agent.Codename,
                diff.Add.Select(id => RoleName(id, guildRoles)).ToList(),
                diff.Remove.Select(id => RoleName(id, guildRoles)).ToList(),
                Warnings(diff, guildRoles));
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            return new DiscordRolePreviewRow(agent.Id, agent.Codename, [], [], ErrorText(ex));
        }
    }

    private async Task<DiscordGuildRoles?> TryGuildRolesAsync(string guildId, CancellationToken cancellationToken)
    {
        try
        {
            return await bot.GetGuildRolesAsync(guildId, cancellationToken);
        }
        catch (Exception ex) when (!IsOwnCancellation(ex, cancellationToken))
        {
            logger.LogWarning("Reading the Discord roles failed: {Error}", ErrorText(ex));
            return null;
        }
    }

    private static string RoleName(string id, DiscordGuildRoles? guildRoles)
        => guildRoles?.Roles.FirstOrDefault(r => r.Id == id)?.Name is { Length: > 0 } name ? name : id;

    /// <summary>Roles the bot would fail on: gone, integration-managed, or not below its own.</summary>
    private static string? Warnings(DiscordRoleDiff diff, DiscordGuildRoles? guildRoles)
    {
        if (guildRoles is null)
        {
            return null;
        }

        var warnings = new List<string>();
        foreach (var id in diff.Add.Concat(diff.Remove).Distinct(StringComparer.Ordinal))
        {
            var role = guildRoles.Roles.FirstOrDefault(r => r.Id == id);
            if (role is null)
            {
                warnings.Add($"Rolle {id} gibt es auf dem Server nicht");
            }
            else if (role.Managed || role.Position >= guildRoles.BotTopPosition)
            {
                warnings.Add($"Bot darf „{role.Name}“ nicht ändern");
            }
        }
        return warnings.Count == 0 ? null : string.Join("; ", warnings);
    }

    /// <summary>Null error = in line, stamp the time; else only the error, the last success stays.</summary>
    private static Task WriteResultAsync(AppDbContext db, string agentId, string? error, CancellationToken cancellationToken)
    {
        // ExecuteUpdate only, no stale write-back
        var row = db.Users.Where(u => u.Id == agentId);
        if (error is not null)
        {
            var text = Shorten(error, MaxErrorChars);
            return row.ExecuteUpdateAsync(s => s.SetProperty(u => u.DiscordRolesSyncError, text), cancellationToken);
        }

        var now = DateTime.UtcNow;
        return row.ExecuteUpdateAsync(s => s
            .SetProperty(u => u.DiscordRolesSyncAt, (DateTime?)now)
            .SetProperty(u => u.DiscordRolesSyncError, (string?)null), cancellationToken);
    }

    /// <summary>Short German error text, never token or stack trace.</summary>
    private static string ErrorText(Exception ex) => ex switch
    {
        DiscordBotException { Status: 401 } => "Discord lehnt den Bot-Token ab (401).",
        DiscordBotException { Status: 403 } =>
            "Bot darf die Rolle nicht ändern (403): Rollen-Hierarchie und Recht „Rollen verwalten“ prüfen.",
        DiscordBotException { Status: 404 } => "Rolle oder Mitglied bei Discord nicht gefunden (404).",
        DiscordBotException { Status: 429 } => "Discord bremst den Bot (429), später erneut versuchen.",
        DiscordBotException { Status: >= 500 } e => $"Discord meldet einen Serverfehler ({e.Status}).",
        DiscordBotException e => e.Message,
        TaskCanceledException or TimeoutException => "Zeitüberschreitung bei Discord.",
        HttpRequestException => "Discord ist nicht erreichbar.",
        JsonException or KeyNotFoundException => "Unerwartete Antwort von Discord.",
        ArgumentException => "Ungültige Discord-Id.",
        _ => $"Abgleich fehlgeschlagen ({ex.GetType().Name}).",
    };

    /// <summary>Own token only; a timeout also surfaces as a cancellation but is a failure.</summary>
    private static bool IsOwnCancellation(Exception ex, CancellationToken cancellationToken)
        => ex is OperationCanceledException && cancellationToken.IsCancellationRequested;

    private static string Reason(string? reason)
        => ReasonPrefix + (string.IsNullOrWhiteSpace(reason) ? DefaultReason : Shorten(reason.Trim(), MaxReasonChars));

    private static string Shorten(string text, int max) => text.Length > max ? text[..max] : text;

    private static bool IsSnowflake(string id)
        => id.Length is >= MinIdChars and <= MaxIdChars && id.All(char.IsAsciiDigit);

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? AsUtc(DateTime? value)
        => value is { } time ? DateTime.SpecifyKind(time, DateTimeKind.Utc) : null;

    private static DateTime? ParseUtc(string? value)
        => DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) ? time : null;

    private static async Task<DiscordRoleSyncSettings> ReadSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        string[] keys =
        [
            SystemSettingKeys.DiscordRoleSyncEnabled, SystemSettingKeys.DiscordGuildId, SystemSettingKeys.DiscordRoleMapping,
        ];
        var values = await db.SystemSettings.AsNoTracking()
            .Where(s => keys.Contains(s.Key))
            .ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

        var enabled = string.Equals(values.GetValueOrDefault(SystemSettingKeys.DiscordRoleSyncEnabled), "true",
            StringComparison.OrdinalIgnoreCase);
        return new DiscordRoleSyncSettings(enabled, Trim(values.GetValueOrDefault(SystemSettingKeys.DiscordGuildId)),
            ParseMapping(values.GetValueOrDefault(SystemSettingKeys.DiscordRoleMapping)));
    }

    /// <summary>Missing or unreadable means nothing mapped, so the bot touches nothing.</summary>
    private static DiscordRoleMapping ParseMapping(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DiscordRoleMapping.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<DiscordRoleMapping>(json, MappingJson) is { } map
                ? Normalize(map)
                : DiscordRoleMapping.Empty;
        }
        catch (JsonException)
        {
            return DiscordRoleMapping.Empty;
        }
    }

    /// <summary>Trimmed ids without blanks and duplicates, no null lists, no empty or unknown entries.</summary>
    private static DiscordRoleMapping Normalize(DiscordRoleMapping map) => new(
        Clean(map.Base),
        CleanEntries(map.ByRank),
        CleanEntries(map.ByDivision),
        Clean(map.Board),
        Clean(map.Administration),
        Clean(map.Admin),
        Clean(map.TeamLead),
        Clean(map.Resident));

    private static IReadOnlyList<string> Clean(IReadOnlyList<string>? ids)
        => DiscordRoleMapping.Ids(ids).Select(id => id.Trim()).Distinct(StringComparer.Ordinal).ToList();

    private static Dictionary<TKey, IReadOnlyList<string>> CleanEntries<TKey>(
        IReadOnlyDictionary<TKey, IReadOnlyList<string>>? entries)
        where TKey : struct, Enum
    {
        var result = new Dictionary<TKey, IReadOnlyList<string>>();
        foreach (var (key, ids) in entries ?? new Dictionary<TKey, IReadOnlyList<string>>())
        {
            var clean = Clean(ids);
            if (Enum.IsDefined(key) && clean.Count > 0)
            {
                result[key] = clean;
            }
        }
        return result;
    }

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
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
