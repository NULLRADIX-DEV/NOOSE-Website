using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;
using NOOSE_Website.Models.Navigation;

namespace NOOSE_Website.Services;

/// <summary>Watches the counter-intelligence rules and the NOOSEI anomalies and rings the leadership bell for new findings.</summary>
public interface IFindingWatchService
{
    /// <summary>One pass over both sources; returns how many findings were newly reported.</summary>
    Task<int> RunAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>The viewer's findings of one source reported since their last look; empty without the counter-intel right.</summary>
    Task<UnseenFindings> GetUnseenAsync(ClaimsPrincipal actor, FindingSource source, CancellationToken cancellationToken = default);

    /// <summary>Stamps one source as looked at up to <paramref name="asOfUtc"/> and reads its bell notice; silent without the right.</summary>
    Task MarkSeenAsync(ClaimsPrincipal actor, FindingSource source, DateTime asOfUtc, CancellationToken cancellationToken = default);
}

/// <summary>Findings a viewer has not looked at yet, and the instant they were read at.</summary>
/// <param name="AsOfUtc">Read before the ledger, so a finding reported meanwhile stays new after the stamp.</param>
public sealed record UnseenFindings(DateTime AsOfUtc, IReadOnlySet<string> Slots)
{
    public static UnseenFindings None(DateTime asOfUtc) => new(asOfUtc, new HashSet<string>());

    /// <summary>Whether the finding of this rule id or anomaly kind about this agent is new to the viewer.</summary>
    public bool IsNew(string key, string agentId) => Slots.Contains(FindingWatchRules.Slot(key, agentId));
}

/// <inheritdoc cref="IFindingWatchService" />
public sealed class FindingWatchService(
    IDbContextFactory<AppDbContext> dbFactory,
    ICounterIntelRuleService rules,
    ILlmAnomalyService anomalies,
    INotificationService notifications,
    INavPreferencesService navPreferences,
    ILogger<FindingWatchService> logger) : IFindingWatchService
{
    public async Task<int> RunAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        var reported = 0;
        try
        {
            reported += await RunRulesAsync(nowUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Finding watch: rule pass failed.");
        }

        try
        {
            reported += await RunNooseiAsync(nowUtc, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Finding watch: NOOSEI pass failed.");
        }
        return reported;
    }

    // ---------------------------------------------------------------- rules

    private async Task<int> RunRulesAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var nowLocal = nowUtc.ToLocalTime();
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            // a rule nobody armed yet starts now: the deploy, or a write path that forgot the stamp
            await db.CounterIntelRules
                .Where(r => r.ReportFrom == null)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.ReportFrom, nowUtc), cancellationToken);
        }

        var active = await ActiveRulesAsync(cancellationToken);
        var watched = active.Where(r => r.Notify && r.ReportFrom is not null).ToList();
        if (watched.Count == 0)
        {
            return 0;
        }

        List<FindingWatchRules.RuleIncident> incidents;
        Dictionary<string, FindingReport> ledger;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var ids = watched.Select(r => r.Id).ToList();
            ledger = (await db.FindingReports.AsNoTracking()
                    .Where(r => r.Source == FindingSource.Rule && ids.Contains(r.Key))
                    .ToListAsync(cancellationToken))
                .ToDictionary(r => FindingWatchRules.Slot(r.Key, r.AgentId));
            var notBefore = watched.Min(r => r.ReportFrom!.Value);
            var events = await CounterIntelEventLoader.LoadAsync(
                db, watched.Select(r => r.Definition).ToList(), cancellationToken, notBefore);
            incidents = FindingWatchRules.Incidents(events, watched, ledger, nowUtc, nowLocal);
        }
        if (incidents.Count == 0 || cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        // past this point a stopping host must not leave a claim without its notice
        var claimed = new List<(string Key, string AgentId)>();
        try
        {
            var current = await RuleStatesAsync(incidents.Select(i => i.RuleId).Distinct().ToList());
            foreach (var incident in incidents)
            {
                var view = watched.First(r => r.Id == incident.RuleId);
                // edited, switched off or silenced while this pass ran: its next pass decides
                if (!current.TryGetValue(incident.RuleId, out var state)
                    || !state.IsActive || !state.Notify || state.ReportFrom != view.ReportFrom)
                {
                    continue;
                }
                ledger.TryGetValue(FindingWatchRules.Slot(incident.RuleId, incident.AgentId), out var row);
                if (await ClaimAsync(FindingSource.Rule, incident.RuleId, incident.AgentId, row,
                        incident.ConsumedUntilUtc, incident.Grade))
                {
                    claimed.Add((incident.RuleId, incident.AgentId));
                }
            }
        }
        catch (Exception ex)
        {
            // what is already claimed still rings below
            logger.LogError(ex, "Finding watch: claiming rule findings failed.");
        }

        if (claimed.Count > 0)
        {
            await NotifyAsync(FindingSource.Rule, claimed, active, weekStartUtc: default, nowUtc);
        }
        await PruneAsync(FindingSource.Rule, nowUtc - FindingWatchRules.RuleRetention);
        return claimed.Count;
    }

    // the cockpit's own cut: active rules in display order, capped like the evaluator caps them
    private async Task<List<CounterIntelRuleView>> ActiveRulesAsync(CancellationToken cancellationToken)
        => (await rules.GetActiveAsync(cancellationToken))
            .OrderBy(r => r.Order)
            .Take(CounterIntelRuleEvaluator.MaxRules)
            .ToList();

    private async Task<Dictionary<string, RuleState>> RuleStatesAsync(List<string> ids)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        return await db.CounterIntelRules.AsNoTracking()
            .Where(r => ids.Contains(r.Id))
            .Select(r => new RuleState(r.Id, r.IsActive, r.Notify, r.ReportFrom))
            .ToDictionaryAsync(r => r.Id, CancellationToken.None);
    }

    // ---------------------------------------------------------------- NOOSEI

    private async Task<int> RunNooseiAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var snapshot = await anomalies.EvaluateAsync(cancellationToken);
        var weekStart = snapshot.WeekStartUtc;

        Dictionary<string, FindingReport> ledger;
        bool armed;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            ledger = (await db.FindingReports.AsNoTracking()
                    .Where(r => r.Source == FindingSource.Noosei)
                    .ToListAsync(cancellationToken))
                .ToDictionary(r => FindingWatchRules.Slot(r.Key, r.AgentId));
            var raw = await db.SystemSettings.AsNoTracking()
                .Where(s => s.Key == SystemSettingKeys.NooseiAnomalyWatchArmedAt)
                .Select(s => s.Value)
                .FirstOrDefaultAsync(cancellationToken);
            armed = DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);
        }
        if (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }

        if (!armed)
        {
            await BaselineAsync(snapshot.Flags, ledger, weekStart);
            await StampArmedAsync(nowUtc);
            return 0;
        }

        var claimed = new List<(string Key, string AgentId)>();
        try
        {
            foreach (var flag in snapshot.Flags)
            {
                var key = flag.Kind.ToString();
                ledger.TryGetValue(FindingWatchRules.Slot(key, flag.AgentId), out var row);
                if (!FindingWatchRules.ShouldReport(row, flag.Grade, weekStart))
                {
                    continue;
                }
                if (await ClaimAsync(FindingSource.Noosei, key, flag.AgentId, row, weekStart,
                        FindingWatchRules.NextGrade(row, flag.Grade, weekStart)))
                {
                    claimed.Add((key, flag.AgentId));
                }
            }
        }
        catch (Exception ex)
        {
            // what is already claimed still rings below
            logger.LogError(ex, "Finding watch: claiming NOOSEI findings failed.");
        }

        if (claimed.Count > 0)
        {
            await NotifyAsync(FindingSource.Noosei, claimed, [], weekStart, nowUtc);
        }
        await PruneAsync(FindingSource.Noosei, weekStart.AddDays(-7));
        return claimed.Count;
    }

    // what the anomaly checks flag right now is known, not news; a row already reported this week keeps its stamp
    private async Task BaselineAsync(IReadOnlyList<LlmAnomalyFlag> flags, Dictionary<string, FindingReport> ledger,
        DateTime weekStart)
    {
        foreach (var flag in flags)
        {
            var key = flag.Kind.ToString();
            ledger.TryGetValue(FindingWatchRules.Slot(key, flag.AgentId), out var row);
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            if (row is null)
            {
                db.FindingReports.Add(new FindingReport
                {
                    Source = FindingSource.Noosei,
                    Key = key,
                    AgentId = flag.AgentId,
                    CoveredUntil = weekStart,
                    Grade = flag.Grade,
                });
                await TrySaveAsync(db);
                continue;
            }
            var grade = FindingWatchRules.NextGrade(row, flag.Grade, weekStart);
            var rows = db.FindingReports.Where(r => r.Id == row.Id);
            if (row.CoveredUntil == weekStart)
            {
                await rows.ExecuteUpdateAsync(s => s.SetProperty(r => r.Grade, grade), CancellationToken.None);
            }
            else
            {
                await rows.ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.CoveredUntil, weekStart)
                    .SetProperty(r => r.Grade, grade)
                    .SetProperty(r => r.ReportedAt, (DateTime?)null), CancellationToken.None);
            }
        }
    }

    private async Task StampArmedAsync(DateTime nowUtc)
    {
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        var value = nowUtc.ToString("o", CultureInfo.InvariantCulture);
        var row = await db.SystemSettings.FirstOrDefaultAsync(
            s => s.Key == SystemSettingKeys.NooseiAnomalyWatchArmedAt, CancellationToken.None);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SystemSettingKeys.NooseiAnomalyWatchArmedAt, Value = value });
        }
        else
        {
            row.Value = value;
        }
        await TrySaveAsync(db);
    }

    // ---------------------------------------------------------------- shared

    /// <summary>Takes a finding for this pass; false when another pass already did, so only one of them rings.</summary>
    /// <remarks>Stamped with the claim's own instant, not the pass start: a panel that read the ledger in between
    /// must still see the finding as reported after its look.</remarks>
    private async Task<bool> ClaimAsync(FindingSource source, string key, string agentId, FindingReport? row,
        DateTime coveredUntil, CounterIntelSeverity grade)
    {
        var nowUtc = DateTime.UtcNow;
        await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
        if (row is not null)
        {
            var read = row.CoveredUntil;
            var readGrade = row.Grade;
            return await db.FindingReports
                .Where(r => r.Id == row.Id && r.CoveredUntil == read && r.Grade == readGrade)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.CoveredUntil, coveredUntil)
                    .SetProperty(r => r.Grade, grade)
                    .SetProperty(r => r.ReportedAt, nowUtc), CancellationToken.None) == 1;
        }

        db.FindingReports.Add(new FindingReport
        {
            Source = source,
            Key = key,
            AgentId = agentId,
            CoveredUntil = coveredUntil,
            Grade = grade,
            ReportedAt = nowUtc,
        });
        return await TrySaveAsync(db);
    }

    // the unique index decides a race between two passes
    private static async Task<bool> TrySaveAsync(AppDbContext db)
    {
        try
        {
            await db.SaveChangesAsync(CancellationToken.None);
            return true;
        }
        catch (DbUpdateException)
        {
            return false;
        }
    }

    /// <summary>One collective notice per leader who has a new finding about someone else.</summary>
    private async Task NotifyAsync(FindingSource source, List<(string Key, string AgentId)> claimed,
        IReadOnlyList<CounterIntelRuleView> active, DateTime weekStartUtc, DateTime nowUtc)
    {
        List<FindingReport> rows;
        List<(string Id, string? Prefs)> recipients;
        await using (var db = await dbFactory.CreateDbContextAsync(CancellationToken.None))
        {
            rows = await db.FindingReports.AsNoTracking()
                .Where(r => r.Source == source && r.ReportedAt != null)
                .ToListAsync(CancellationToken.None);
            recipients = (await LeadershipAsync(db))
                .Select(r => (r.Id, r.NavPreferencesJson))
                .ToList();
        }

        var byRule = active.ToDictionary(r => r.Id);
        foreach (var (recipientId, prefsJson) in recipients)
        {
            // nobody hears about their own finding
            if (!claimed.Any(c => !string.Equals(c.AgentId, recipientId, StringComparison.Ordinal)))
            {
                continue;
            }
            var seen = Seen(Prefs(prefsJson), source);
            var unseen = rows.Where(r => source == FindingSource.Noosei
                    ? FindingWatchRules.IsUnseenNoosei(r, recipientId, seen, weekStartUtc)
                    : FindingWatchRules.IsUnseenRule(r, recipientId, seen, byRule.GetValueOrDefault(r.Key), nowUtc))
                .ToList();
            if (unseen.Count == 0)
            {
                continue;
            }

            var title = FindingWatchRules.Title(source, unseen.Count, Names(source, unseen, byRule));
            try
            {
                await notifications.NotifyOnceAsync(recipientId, FindingWatchRules.Type(source), title,
                    FindingWatchRules.Href(source), CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Finding watch: a notice could not be delivered.");
            }
        }
    }

    // leadership as every leadership fan-out reads it: never team leads, partners or the demo visitor
    private static Task<List<Recipient>> LeadershipAsync(AppDbContext db)
        => db.Users.AsNoTracking()
            .OnlySelectable()
            .Where(u => u.IsAdmin || u.Rank >= Rank.SupervisorySpecialAgent)
            .Where(u => u.Id != DemoIdentity.AgentId)
            .Select(u => new Recipient(u.Id, u.NavPreferencesJson))
            .ToListAsync(CancellationToken.None);

    private static List<string> Names(FindingSource source, List<FindingReport> unseen,
        Dictionary<string, CounterIntelRuleView> byRule)
    {
        if (source == FindingSource.Noosei)
        {
            return unseen
                .Select(r => Enum.TryParse<LlmAnomalyKind>(r.Key, out var kind) ? kind : (LlmAnomalyKind?)null)
                .OfType<LlmAnomalyKind>()
                .Distinct()
                .Order()
                .Select(LlmAnomalyKindDisplay.Name)
                .ToList();
        }
        return unseen
            .Select(r => byRule.GetValueOrDefault(r.Key))
            .OfType<CounterIntelRuleView>()
            .DistinctBy(r => r.Id)
            .OrderBy(r => r.Order)
            .Select(r => r.Name)
            .ToList();
    }

    private async Task PruneAsync(FindingSource source, DateTime cutoffUtc)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(CancellationToken.None);
            await db.FindingReports
                .Where(r => r.Source == source && r.CoveredUntil < cutoffUtc
                            && (r.ReportedAt == null || r.ReportedAt < cutoffUtc))
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Finding watch: pruning old rows failed.");
        }
    }

    // ---------------------------------------------------------------- panels

    public async Task<UnseenFindings> GetUnseenAsync(ClaimsPrincipal actor, FindingSource source,
        CancellationToken cancellationToken = default)
    {
        var asOf = DateTime.UtcNow;
        var agentId = actor.GetAgentId();
        if (!actor.MayCounterIntel() || string.IsNullOrEmpty(agentId))
        {
            return UnseenFindings.None(asOf);
        }

        var seen = Seen(await navPreferences.GetAsync(agentId, cancellationToken), source);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.FindingReports.AsNoTracking()
            .Where(r => r.Source == source && r.ReportedAt != null && r.AgentId != agentId)
            .Where(r => seen == null || r.ReportedAt > seen)
            .ToListAsync(cancellationToken);

        IEnumerable<FindingReport> unseen;
        if (source == FindingSource.Noosei)
        {
            var weekStart = CurrentWeekStartUtc();
            unseen = rows.Where(r => FindingWatchRules.IsUnseenNoosei(r, agentId, seen, weekStart));
        }
        else
        {
            var byRule = (await ActiveRulesAsync(cancellationToken)).ToDictionary(r => r.Id);
            unseen = rows.Where(r =>
                FindingWatchRules.IsUnseenRule(r, agentId, seen, byRule.GetValueOrDefault(r.Key), asOf));
        }
        return new UnseenFindings(asOf,
            unseen.Select(r => FindingWatchRules.Slot(r.Key, r.AgentId)).ToHashSet());
    }

    public async Task MarkSeenAsync(ClaimsPrincipal actor, FindingSource source, DateTime asOfUtc,
        CancellationToken cancellationToken = default)
    {
        var agentId = actor.GetAgentId();
        // runs on page load: a viewer without the right is simply not stamped
        if (!actor.MayCounterIntel() || string.IsNullOrEmpty(agentId))
        {
            return;
        }
        await navPreferences.SetFindingsSeenAsync(agentId, source, asOfUtc, cancellationToken);
        await notifications.TargetAsReadMarkAsync(actor, FindingWatchRules.Type(source),
            FindingWatchRules.Href(source), asOfUtc, cancellationToken);
    }

    private static DateTime CurrentWeekStartUtc()
    {
        var (year, week) = IsoWeekPeriod.Current();
        return IsoWeekPeriod.Start(year, week).ToUniversalTime();
    }

    private static DateTime? Seen(NavPreferences prefs, FindingSource source)
        => source == FindingSource.Noosei ? prefs.NooseiAnomaliesSeenUtc : prefs.CounterIntelSeenUtc;

    private static NavPreferences Prefs(string? json)
    {
        try
        {
            return NavPreferences.FromJson(json);
        }
        catch (Exception)
        {
            return new NavPreferences(); /* a broken blob counts as never looked */
        }
    }

    private sealed record RuleState(string Id, bool IsActive, bool Notify, DateTime? ReportFrom);

    private sealed record Recipient(string Id, string? NavPreferencesJson);
}
