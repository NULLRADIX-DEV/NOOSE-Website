using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.CounterIntel;

namespace NOOSE_Website.Services;

/// <summary>Counter-intelligence cockpit: read-only aggregation over the access log. Leadership-only (not read-only supervisors).</summary>
public interface ICounterIntelService
{
    Task<CounterIntelOverview> GetOverviewAsync(ClaimsPrincipal actor, int days = 30, CancellationToken cancellationToken = default);
    Task<CounterIntelHeatmap> GetHeatmapAsync(ClaimsPrincipal actor, int days = 30, CancellationToken cancellationToken = default);
    Task<AgentAccessProfile?> GetAgentProfileAsync(ClaimsPrincipal actor, string agentId, int days = 30, CancellationToken cancellationToken = default);

    /// <summary>Findings of every active rule; each rule brings its own observation window.</summary>
    Task<IReadOnlyList<InsiderFlag>> GetFlagsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Findings of a single, possibly unsaved rule — the editor's preview.</summary>
    Task<IReadOnlyList<InsiderFlag>> PreviewAsync(ClaimsPrincipal actor, CounterIntelRuleView rule, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentOption>> GetAgentsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ICounterIntelService" />
public class CounterIntelService(IDbContextFactory<AppDbContext> dbFactory, ICounterIntelRuleService rules) : ICounterIntelService
{
    private const int MaxRows = 30000;
    private const int MaxHeatmapAgents = 30;
    private const int RecentAccessCount = 25;

    public async Task<CounterIntelOverview> GetOverviewAsync(ClaimsPrincipal actor, int days = 30, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await LoadAsync(db, days, cancellationToken);
        return new CounterIntelOverview(
            rows.Count,
            rows.Select(r => r.AgentId).Distinct().Count(),
            rows.Select(r => $"{r.EntityType}:{r.EntityId}").Distinct().Count(),
            rows.Count(r => IsOffHours(r.LocalTimestamp)),
            days);
    }

    public async Task<CounterIntelHeatmap> GetHeatmapAsync(ClaimsPrincipal actor, int days = 30, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await LoadAsync(db, days, cancellationToken);

        var agents = rows.GroupBy(r => r.AgentId)
            .Select(g =>
            {
                var hours = new int[24];
                foreach (var r in g)
                {
                    hours[r.LocalTimestamp.Hour]++;
                }
                return new HeatAgent(g.Key, NameOr(g.First().AgentName), hours);
            })
            .OrderByDescending(a => a.Hours.Sum())
            .Take(MaxHeatmapAgents)
            .ToList();

        var max = agents.SelectMany(a => a.Hours).DefaultIfEmpty(0).Max();
        return new CounterIntelHeatmap(agents, max);
    }

    public async Task<AgentAccessProfile?> GetAgentProfileAsync(ClaimsPrincipal actor, string agentId, int days = 30, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await LoadAsync(db, days, cancellationToken, agentId);
        if (rows.Count == 0)
        {
            return null;
        }
        var byType = rows.GroupBy(r => r.EntityType)
            .Select(g => new TypeCount(g.Key, g.Count()))
            .OrderByDescending(t => t.Count)
            .ToList();
        var recent = rows.OrderByDescending(r => r.LocalTimestamp)
            .Take(RecentAccessCount)
            .Select(r => new RecentAccess(r.LocalTimestamp, r.EntityType, r.EntityId, Href(r.EntityType, r.EntityId)))
            .ToList();
        return new AgentAccessProfile(
            agentId, NameOr(rows[0].AgentName), rows.Count,
            rows.Select(r => $"{r.EntityType}:{r.EntityId}").Distinct().Count(),
            rows.Count(r => IsOffHours(r.LocalTimestamp)),
            byType, recent);
    }

    public async Task<IReadOnlyList<InsiderFlag>> GetFlagsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        var active = await rules.GetActiveAsync(cancellationToken);
        if (active.Count == 0)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var events = await CounterIntelEventLoader.LoadAsync(
            db, active.Select(r => r.Definition).ToList(), cancellationToken);
        return CounterIntelRuleEvaluator.Evaluate(events, active, DateTime.Now);
    }

    public async Task<IReadOnlyList<InsiderFlag>> PreviewAsync(
        ClaimsPrincipal actor, CounterIntelRuleView rule, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var events = await CounterIntelEventLoader.LoadAsync(db, [rule.Definition], cancellationToken);
        // preview ignores the active switch: the point is to try a rule before switching it on
        return CounterIntelRuleEvaluator.EvaluateOne(events, rule with { IsActive = true }, DateTime.Now);
    }

    public async Task<IReadOnlyList<AgentOption>> GetAgentsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadershipNoReader(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // only agents that actually accessed something; names come from the roster, never from the log
        var actorIds = await db.AccessLogs.AsNoTracking()
            .Where(a => a.AgentId != null)
            .Select(a => a.AgentId!)
            .Distinct()
            .ToListAsync(cancellationToken);
        return (await AgentDirectory.ByIdsAsync(db, actorIds, cancellationToken))
            .Select(a => new AgentOption(a.Id, a.Codename))
            .ToList();
    }

    // recent access rows, system rows + read-only supervisors removed, timestamps localized; one agent's rows are
    // filtered before the cap, so a busy office never crowds a single profile out of it
    private static async Task<List<AccessRow>> LoadAsync(AppDbContext db, int days, CancellationToken ct,
        string? agentId = null)
    {
        var since = DateTime.UtcNow.AddDays(-days);
        var excluded = await OnlyReaderIdsAsync(db, ct);
        var query = db.AccessLogs.AsNoTracking().Where(a => a.AgentId != null && a.Timestamp >= since);
        if (agentId is not null)
        {
            query = query.Where(a => a.AgentId == agentId);
        }
        var rows = await query
            .OrderByDescending(a => a.Timestamp)
            .Take(MaxRows)
            .Select(a => new { a.AgentId, a.AgentName, a.Timestamp, a.EntityType, a.EntityId })
            .ToListAsync(ct);
        return rows
            .Where(r => !excluded.Contains(r.AgentId!))
            .Select(r => new AccessRow(r.AgentId!, r.AgentName, r.Timestamp.ToLocalTime(), r.EntityType, r.EntityId))
            .ToList();
    }

    private static async Task<HashSet<string>> OnlyReaderIdsAsync(AppDbContext db, CancellationToken ct)
        => (await db.Users.Where(u => u.IsTeamLead && !u.IsAdmin).Select(u => u.Id).ToListAsync(ct)).ToHashSet();

    // the overview KPI keeps a fixed 22–6 window; rules define their own
    private static bool IsOffHours(DateTime local) => CounterIntelRuleEvaluator.InHourWindow(local.Hour, 22, 6);

    // never surface a raw agent id as a name
    private static string NameOr(string? name) => string.IsNullOrWhiteSpace(name) ? "(unbenannt)" : name;

    private static string? Href(string type, string id) => type switch
    {
        nameof(NOOSE_Website.Data.Entities.People.Person) => $"/personen/{id}",
        nameof(NOOSE_Website.Data.Entities.Factions.Faction) => $"/fraktionen/{id}",
        nameof(NOOSE_Website.Data.Entities.Groups.PersonGroup) => $"/personengruppen/{id}",
        nameof(NOOSE_Website.Data.Entities.Parties.Party) => $"/parteien/{id}",
        nameof(NOOSE_Website.Data.Entities.Operations.Operation) => $"/operationen/{id}",
        nameof(NOOSE_Website.Data.Entities.Cases.Case) => $"/vorgaenge/{id}",
        nameof(NOOSE_Website.Data.Entities.Taskforces.Taskforce) => $"/taskforces/{id}",
        _ => null,
    };
}
