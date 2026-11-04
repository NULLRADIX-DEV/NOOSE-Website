using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services;

/// <inheritdoc />
public sealed class CalendarFeedService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICalendarService calendar,
    IUserClaimsPrincipalFactory<Agent> principals,
    IDiscordWebhookService discord,
    ILogger<CalendarFeedService> logger) : ICalendarFeedService
{
    public const string AccessType = "Kalender-Abo";
    private const string AccessId = "Abruf";
    private static readonly TimeSpan LogGap = TimeSpan.FromMinutes(30);

    public async Task<CalendarFeedStatus> GetStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var id = actor.GetAgentId();
        if (string.IsNullOrEmpty(id))
        {
            return new CalendarFeedStatus(false, null, false);
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Users.AsNoTracking().Where(u => u.Id == id)
            .Select(u => new { u.CalendarFeedTokenHash, u.CalendarFeedCreatedAt, u.CalendarFeedNeutralTitles })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? new CalendarFeedStatus(false, null, false)
            : new CalendarFeedStatus(row.CalendarFeedTokenHash is not null, row.CalendarFeedCreatedAt,
                row.CalendarFeedNeutralTitles == true);
    }

    public async Task<CalendarFeedLink> CreateAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        RequireHolder(actor);
        var id = actor.GetAgentId()!;
        var token = CalendarFeedToken.Generate();
        var hash = CalendarFeedToken.Hash(token);
        var now = DateTime.UtcNow;

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var had = await db.Users.AnyAsync(u => u.Id == id && u.CalendarFeedTokenHash != null, cancellationToken);
        // the stored row has the last word, not the claims, which can be 30 s old
        var written = await db.Users
            .Where(u => u.Id == id && u.Status == AgentStatus.Active && u.PartnerAgency == null && !u.IsTeamLead)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.CalendarFeedTokenHash, hash)
                .SetProperty(u => u.CalendarFeedCreatedAt, now), cancellationToken);
        if (written == 0)
        {
            throw new UnauthorizedAccessException(NotAllowed);
        }

        await AuditAsync(db, id, actor, had ? "erneuert" : "angelegt", cancellationToken);
        var config = await discord.GetConfigAsync(cancellationToken);
        return new CalendarFeedLink(CalendarFeedRules.FeedUrl(config.SiteBaseUrl, token));
    }

    public async Task RevokeAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var id = actor.GetAgentId();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var written = await db.Users.Where(u => u.Id == id && u.CalendarFeedTokenHash != null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(u => u.CalendarFeedTokenHash, (string?)null)
                .SetProperty(u => u.CalendarFeedCreatedAt, (DateTime?)null), cancellationToken);
        if (written > 0)
        {
            await AuditAsync(db, id, actor, "widerrufen", cancellationToken);
        }
    }

    public async Task SetNeutralTitlesAsync(bool neutral, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        var id = actor.GetAgentId();
        if (string.IsNullOrEmpty(id))
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Users.Where(u => u.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.CalendarFeedNeutralTitles, (bool?)neutral), cancellationToken);
    }

    public async Task<string?> RenderAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (!CalendarFeedToken.HasValidShape(token))
        {
            return null;
        }
        var hash = CalendarFeedToken.Hash(token!);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.CalendarFeedTokenHash == hash, cancellationToken);
        if (agent is null || !CalendarFeedToken.Matches(agent.CalendarFeedTokenHash, token)
            || !CalendarFeedRules.MayHold(agent))
        {
            return null;
        }

        // the principal the login would carry, so the aggregator decides exactly as it does on the page
        var principal = await principals.CreateAsync(agent);
        if (!CalendarFeedRules.MayHold(principal)
            || !string.Equals(principal.GetAgentId(), agent.Id, StringComparison.Ordinal))
        {
            return null;
        }

        var now = DateTime.UtcNow;
        var from = now.AddDays(-CalendarFeedRules.DaysBack);
        var until = now.AddDays(CalendarFeedRules.DaysAhead);
        var mine = await calendar.GetEntriesAsync(from, until, principal, CalendarMode.My, cancellationToken);
        var house = await calendar.GetEntriesAsync(from, until, principal, CalendarMode.Authority, cancellationToken);

        var config = await discord.GetConfigAsync(cancellationToken);
        var site = WantedShortLink.SiteBase(config.SiteBaseUrl);
        var host = Uri.TryCreate(site, UriKind.Absolute, out var uri) ? uri.Host : "fibhq.com";
        var events = CalendarFeedRules.Project(mine.Concat(house), agent.CalendarFeedNeutralTitles == true,
            site, host, TimeZoneInfo.Local, now);
        var body = IcsWriter.Serialize("-//FIB//Kalender-Abo//DE", "FIB Kalender", events);

        await LogFetchAsync(db, agent, now, cancellationToken);
        return body;
    }

    private const string NotAllowed = "Das Kalender-Abo gibt es nur für aktive Agenten der Behörde.";

    private static void RequireHolder(ClaimsPrincipal actor)
    {
        Permission.RequireWriteAccess(actor);
        if (!CalendarFeedRules.MayHold(actor))
        {
            throw new UnauthorizedAccessException(NotAllowed);
        }
    }

    private static async Task AuditAsync(AppDbContext db, string agentId, ClaimsPrincipal actor, string state, CancellationToken ct)
    {
        // state words only, never the secret or its hash
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agentId, AuditAction.Modified, actor,
            ManualAudit.Change("Kalender-Abo", null, state)));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>One access-log row per half hour at most: a calendar app polls by the hour and would drown the log.</summary>
    private async Task LogFetchAsync(AppDbContext db, Agent agent, DateTime now, CancellationToken ct)
    {
        try
        {
            var since = now - LogGap;
            if (await db.AccessLogs.AnyAsync(l => l.AgentId == agent.Id && l.EntityType == AccessType && l.Timestamp >= since, ct))
            {
                return;
            }
            db.AccessLogs.Add(new AccessLog
            {
                Timestamp = now,
                AgentId = agent.Id,
                AgentName = agent.Codename,
                EntityType = AccessType,
                EntityId = AccessId,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // the feed outlives a log hiccup; nothing secret is in the message
            logger.LogWarning(ex, "Kalender-Abo: Abruf konnte nicht protokolliert werden.");
        }
    }
}
