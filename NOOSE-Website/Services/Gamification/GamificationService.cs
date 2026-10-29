using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Gamification;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Gamification;

namespace NOOSE_Website.Services;

/// <summary>Read-only performance stats + milestone badges. Counts derive from IAuditable.CreatedById,
/// classification/observation authorship and audited case-status transitions; badges are awarded by a daily sweep.</summary>
public interface IGamificationService
{
    Task<AgentStats> GetStatsAsync(string agentId, CancellationToken cancellationToken = default);
    /// <summary>Leaderboard for <paramref name="period"/>; topN caps each slice on its own.</summary>
    Task<LeaderboardView> GetLeaderboardAsync(GamificationPeriod period, int topN = 25, CancellationToken cancellationToken = default);
    /// <summary>Leaderboard over the last <paramref name="windowDays"/> days (0 or less = all time); topN caps each slice on its own.</summary>
    Task<LeaderboardView> GetLeaderboardAsync(int windowDays, int topN = 25, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<BadgeView>> GetBadgesAsync(string agentId, CancellationToken cancellationToken = default);
    /// <summary>Points per agent over the last <paramref name="windowDays"/> days, scored as the leaderboard scores
    /// them; every account that earned any, departed ones included.</summary>
    Task<IReadOnlyDictionary<string, int>> GetPointsAsync(int windowDays, CancellationToken cancellationToken = default);
    /// <summary>Award any newly-earned badges across all agents; returns how many were granted. Idempotent.</summary>
    Task<int> SweepAsync(CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGamificationService" />
public sealed class GamificationService(IDbContextFactory<AppDbContext> dbFactory) : IGamificationService
{
    // out of the running from Supervisory Special Agent up; rank only, so an admin with a low rank keeps
    // competing - deliberately narrower than AgentPrincipalExtensions.IsLeadership()
    private const Rank LeadershipFloor = Rank.SupervisorySpecialAgent;

    // one predicate, negated for the other slice: Rank is nullable and a >=/< pair is false on BOTH sides for null
    private static bool OutOfCompetition(Rank? rank) => rank >= LeadershipFloor;

    public async Task<AgentStats> GetStatsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return AgentStats.Empty;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var acc = await ComputeForAgentAsync(db, agentId, cancellationToken);
        var badges = await db.AgentBadges.CountAsync(b => b.AgentId == agentId, cancellationToken);
        return acc.ToStats(badges);
    }

    public Task<LeaderboardView> GetLeaderboardAsync(
        GamificationPeriod period, int topN = 25, CancellationToken cancellationToken = default)
        => BuildLeaderboardAsync(Cutoff(period), topN, cancellationToken);

    public Task<LeaderboardView> GetLeaderboardAsync(
        int windowDays, int topN = 25, CancellationToken cancellationToken = default)
        => BuildLeaderboardAsync(windowDays > 0 ? DateTime.UtcNow.AddDays(-windowDays) : null, topN, cancellationToken);

    public async Task<IReadOnlyDictionary<string, int>> GetPointsAsync(
        int windowDays, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var all = await ComputeAllAsync(db, DateTime.UtcNow.AddDays(-windowDays), cancellationToken);
        return all.ToDictionary(e => e.Key, e => e.Value.ToStats(0).Points, StringComparer.Ordinal);
    }

    private async Task<LeaderboardView> BuildLeaderboardAsync(
        DateTime? since, int topN, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var all = await ComputeAllAsync(db, since, cancellationToken);

        // ranking is over internal, active agents only; team leads are RP-invisible and never listed
        var agents = await db.Users.OnlySelectable()
            .Select(u => new { u.Id, u.Codename, u.Rank })
            .ToListAsync(cancellationToken);

        // score and order once, then split; topN caps each slice so leadership never displaces a placed agent
        var scored = agents
            .Select(a => new { a.Id, a.Codename, a.Rank, Acc = all.GetValueOrDefault(a.Id) ?? new StatAcc() })
            .Select(x => new { x.Id, x.Codename, x.Rank, x.Acc, Points = x.Acc.ToStats(0).Points })
            .Where(x => x.Points > 0)
            .OrderByDescending(x => x.Points).ThenBy(x => x.Codename, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new LeaderboardView(
            scored.Where(x => !OutOfCompetition(x.Rank)).Take(topN)
                .Select((x, i) => Row(x.Id, x.Codename, x.Acc, x.Points, i + 1)).ToList(),
            scored.Where(x => OutOfCompetition(x.Rank)).Take(topN)
                .Select(x => Row(x.Id, x.Codename, x.Acc, x.Points, 0)).ToList());
    }

    private static LeaderboardEntry Row(string id, string codename, StatAcc acc, int points, int position)
        => new(position, id, string.IsNullOrWhiteSpace(codename) ? "(unbenannt)" : codename, points,
            acc.Records, acc.Docs, acc.Links, acc.Classifications, acc.Observations, acc.SolvedCases);

    public async Task<IReadOnlyList<BadgeView>> GetBadgesAsync(string agentId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId))
        {
            return Array.Empty<BadgeView>();
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var awarded = await db.AgentBadges.Where(b => b.AgentId == agentId)
            .Select(b => new { b.BadgeKey, b.AwardedAt })
            .ToListAsync(cancellationToken);
        return awarded
            .Select(a => (Def: BadgeCatalog.Find(a.BadgeKey), a.AwardedAt))
            .Where(x => x.Def is not null)
            .OrderByDescending(x => x.AwardedAt)
            .Select(x => new BadgeView(x.Def!.Key, x.Def.Label, x.Def.Icon, x.Def.Description, x.AwardedAt))
            .ToList();
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var all = await ComputeAllAsync(db, null, cancellationToken);
        var existing = (await db.AgentBadges.Select(b => new { b.AgentId, b.BadgeKey }).ToListAsync(cancellationToken))
            .Select(x => (x.AgentId, x.BadgeKey)).ToHashSet();

        var now = DateTime.UtcNow;
        var added = 0;
        foreach (var (agentId, acc) in all)
        {
            var stats = acc.ToStats(0);
            foreach (var def in BadgeCatalog.All)
            {
                // unique (AgentId, BadgeKey) index is the hard guard; the set avoids redundant inserts on overlapping sweeps
                if (def.Earned(stats) && existing.Add((agentId, def.Key)))
                {
                    db.AgentBadges.Add(new AgentBadge { AgentId = agentId, BadgeKey = def.Key, AwardedAt = now });
                    added++;
                }
            }
        }
        if (added > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return added;
    }

    private static DateTime? Cutoff(GamificationPeriod period) => period switch
    {
        GamificationPeriod.Week => DateTime.UtcNow.AddDays(-7),
        GamificationPeriod.Month => DateTime.UtcNow.AddDays(-30),
        _ => null,
    };

    // one grouped pass per contribution source, keyed by the crediting agent id
    private static async Task<Dictionary<string, StatAcc>> ComputeAllAsync(AppDbContext db, DateTime? since, CancellationToken ct)
    {
        var acc = new Dictionary<string, StatAcc>(StringComparer.Ordinal);
        StatAcc Get(string id)
        {
            if (!acc.TryGetValue(id, out var s))
            {
                s = new StatAcc();
                acc[id] = s;
            }
            return s;
        }

        async Task AddCreated<T>(IQueryable<T> set, Action<StatAcc, int> apply) where T : class, IAuditable
        {
            var rows = await set
                .Where(x => x.CreatedById != null && (since == null || x.CreatedAt >= since))
                .GroupBy(x => x.CreatedById!)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToListAsync(ct);
            foreach (var r in rows)
            {
                apply(Get(r.Id), r.Count);
            }
        }

        await AddCreated(db.People, (s, n) => s.Records += n);
        await AddCreated(db.Factions, (s, n) => s.Records += n);
        await AddCreated(db.PersonGroups, (s, n) => s.Records += n);
        await AddCreated(db.Parties, (s, n) => s.Records += n);
        await AddCreated(db.Operations, (s, n) => s.Records += n);
        await AddCreated(db.Cases, (s, n) => s.Records += n);
        await AddCreated(db.PersonDocs, (s, n) => s.Docs += n);
        await AddCreated(db.Links, (s, n) => s.Links += n);

        // classifications credited to the deciding agent
        foreach (var r in await db.ClassificationHistory
            .Where(h => h.AgentId != null && (since == null || h.Timestamp >= since))
            .GroupBy(h => h.AgentId!).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct))
        {
            Get(r.Id).Classifications += r.Count;
        }

        // observations credited to the observing agent
        foreach (var r in await db.Observations
            .Where(o => o.ObservingAgentId != null && o.ObservingAgentId != "" && (since == null || o.CreatedAt >= since))
            .GroupBy(o => o.ObservingAgentId!).Select(g => new { Id = g.Key, Count = g.Count() }).ToListAsync(ct))
        {
            Get(r.Id).Observations += r.Count;
        }

        // solved cases: DISTINCT cases an agent ever transitioned to Completed (a case re-completed after reopening
        // counts once), and only cases that still exist — soft-deleted cases drop out just like the Records count
        var solved = new HashSet<(string Agent, string Case)>();
        foreach (var a in await db.AuditLogs
            .Where(x => x.AgentId != null && x.EntityType == nameof(Case) && x.Action == AuditAction.Modified
                && x.ChangesJson != null && x.ChangesJson.Contains("Status") && (since == null || x.Timestamp >= since))
            .Select(x => new { x.AgentId, x.EntityId, x.ChangesJson })
            .ToListAsync(ct))
        {
            if (IsCompletedTransition(a.ChangesJson))
            {
                solved.Add((a.AgentId!, a.EntityId));
            }
        }
        foreach (var (agent, _) in await LiveSolvedAsync(db, solved, ct))
        {
            Get(agent).SolvedCases++;
        }

        return acc;
    }

    // keep only (agent, case) pairs whose case still exists (the global soft-delete filter drops deleted ones)
    private static async Task<List<(string Agent, string Case)>> LiveSolvedAsync(
        AppDbContext db, HashSet<(string Agent, string Case)> solved, CancellationToken ct)
    {
        if (solved.Count == 0)
        {
            return new List<(string, string)>();
        }
        var caseIds = solved.Select(s => s.Case).Distinct().ToList();
        var live = (await db.Cases.Where(c => caseIds.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
        return solved.Where(s => live.Contains(s.Case)).ToList();
    }

    // single-agent stats without materialising every agent (profile views are hot; avoid the org-wide grouped scans)
    private static async Task<StatAcc> ComputeForAgentAsync(AppDbContext db, string agentId, CancellationToken ct)
    {
        var acc = new StatAcc();
        Task<int> Created<T>(IQueryable<T> set) where T : class, IAuditable
            => set.CountAsync(x => x.CreatedById == agentId, ct);

        acc.Records += await Created(db.People);
        acc.Records += await Created(db.Factions);
        acc.Records += await Created(db.PersonGroups);
        acc.Records += await Created(db.Parties);
        acc.Records += await Created(db.Operations);
        acc.Records += await Created(db.Cases);
        acc.Docs = await Created(db.PersonDocs);
        acc.Links = await Created(db.Links);
        acc.Classifications = await db.ClassificationHistory.CountAsync(h => h.AgentId == agentId, ct);
        acc.Observations = await db.Observations.CountAsync(o => o.ObservingAgentId == agentId, ct);

        var solved = new HashSet<(string Agent, string Case)>();
        foreach (var a in await db.AuditLogs
            .Where(x => x.AgentId == agentId && x.EntityType == nameof(Case) && x.Action == AuditAction.Modified
                && x.ChangesJson != null && x.ChangesJson.Contains("Status"))
            .Select(x => new { x.EntityId, x.ChangesJson })
            .ToListAsync(ct))
        {
            if (IsCompletedTransition(a.ChangesJson))
            {
                solved.Add((agentId, a.EntityId));
            }
        }
        acc.SolvedCases = (await LiveSolvedAsync(db, solved, ct)).Count;
        return acc;
    }

    // ChangesJson is {"Status":[old,new]}; new is the enum value (number or name)
    private static bool IsCompletedTransition(string? changesJson)
    {
        if (string.IsNullOrEmpty(changesJson))
        {
            return false;
        }
        try
        {
            using var doc = JsonDocument.Parse(changesJson);
            if (!doc.RootElement.TryGetProperty("Status", out var el)
                || el.ValueKind != JsonValueKind.Array || el.GetArrayLength() < 2)
            {
                return false;
            }
            var next = el[1];
            return next.ValueKind switch
            {
                JsonValueKind.Number => next.TryGetInt32(out var v) && v == (int)CaseStatus.Completed,
                JsonValueKind.String => string.Equals(next.GetString(), nameof(CaseStatus.Completed), StringComparison.OrdinalIgnoreCase),
                _ => false,
            };
        }
        catch (JsonException)
        {
            return false; /* malformed change payload */
        }
    }

    private sealed class StatAcc
    {
        public int Records;
        public int Docs;
        public int Links;
        public int Classifications;
        public int Observations;
        public int SolvedCases;

        public AgentStats ToStats(int badges) => new(Records, Docs, Links, Classifications, Observations, SolvedCases, badges);
    }
}
