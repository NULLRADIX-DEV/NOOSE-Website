using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Llm;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>Weekly NOOSEI token quota: status, lazy period close, charging, and the AI owner's corrections.</summary>
public interface ILlmQuotaService
{
    /// <summary>Quota of one agent for the running ISO week; closes any elapsed weeks first.</summary>
    Task<LlmQuotaStatus> GetStatusAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Quota of every selectable agent, for the overview.</summary>
    Task<IReadOnlyList<LlmQuotaStatus>> GetAllStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Closed weeks of one agent, newest first — the audit trail of the carry chain.</summary>
    Task<List<LlmQuotaPeriod>> GetPeriodsAsync(string agentId, ClaimsPrincipal actor, int max = 12, CancellationToken cancellationToken = default);

    /// <summary>Manual corrections of one agent, newest first.</summary>
    Task<List<LlmQuotaAdjustment>> GetAdjustmentsAsync(string agentId, ClaimsPrincipal actor, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>Pre-flight: throws when the actor may not use NOOSEI or has nothing left this week.</summary>
    Task<LlmQuotaStatus> EnsureAvailableAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken = default, int? boostPercent = null, LlmFeature feature = LlmFeature.Chat);

    /// <summary>Books a finished call against the running week and returns what it cost.</summary>
    Task<LlmQuotaCharge> TryChargeAsync(LlmChargeInput input, CancellationToken cancellationToken = default);

    /// <summary>Sets or clears (null) an agent's individual weekly quota.</summary>
    Task SetOverrideAsync(string agentId, long? amount, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Signed manual correction of the running week (positive tops up, negative deducts).</summary>
    Task TopUpAsync(string agentId, long tokens, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Gives the running week back in full by booking a correction equal to what is consumed.</summary>
    Task ResetAsync(string agentId, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILlmQuotaService" />
public class LlmQuotaService(
    IDbContextFactory<AppDbContext> dbFactory,
    ILlmQuotaConfigService configService,
    INooseiProviderService providerService,
    IOptions<LlmOptions> options,
    ILogger<LlmQuotaService> logger) : ILlmQuotaService
{
    public async Task<LlmQuotaStatus> GetStatusAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireQuotaRead(actor, agentId);
        return await StatusAsync(agentId, cancellationToken);
    }

    /// <summary>Unguarded status, for the pre-flight and the charge — both already know whose quota it is.</summary>
    /// <param name="boostPercent">
    /// The surcharge the caller has already resolved. The gateway resolves the upstream once per turn and hands it
    /// in, so the allowance is judged against the same boost the request then spends under - the ten-second cache
    /// could otherwise turn over between the check here and the charge afterwards.
    /// </param>
    private async Task<LlmQuotaStatus> StatusAsync(string agentId, CancellationToken cancellationToken, int? boostPercent = null)
    {
        var config = await configService.GetAsync(cancellationToken);
        var boost = boostPercent ?? (await providerService.GetStateAsync(cancellationToken)).BoostPercent;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        var snapshot = await QuotaSnapshot.LoadAsync(db, [agentId], cancellationToken);
        return await BuildStatusAsync(db, agent, config, boost, snapshot, cancellationToken);
    }

    public async Task<IReadOnlyList<LlmQuotaStatus>> GetAllStatusAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireQuotaRead(actor);
        var config = await configService.GetAsync(cancellationToken);
        var boost = (await providerService.GetStateAsync(cancellationToken)).BoostPercent;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Users.AsNoTracking().OnlySelectable()
            .OrderBy(a => a.Codename)
            .ToListAsync(cancellationToken);
        // partner spenders; not a picker
        agents = agents.Concat(await PartnerSpendersAsync(db, cancellationToken)).OrderBy(a => a.Codename).ToList();

        // one snapshot for the whole roster: the ledger asked seven questions per agent, which is a synchronous
        // render waiting on two hundred round trips before it paints a single row
        var snapshot = await QuotaSnapshot.LoadAsync(db, agents.Select(a => a.Id).ToList(), cancellationToken);
        var list = new List<LlmQuotaStatus>(agents.Count);
        foreach (var agent in agents)
        {
            list.Add(await BuildStatusAsync(db, agent, config, boost, snapshot, cancellationToken));
        }
        return list;
    }

    public async Task<List<LlmQuotaPeriod>> GetPeriodsAsync(string agentId, ClaimsPrincipal actor, int max = 12, CancellationToken cancellationToken = default)
    {
        Permission.RequireQuotaRead(actor, agentId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.LlmQuotaPeriods.AsNoTracking()
            .Where(p => p.AgentId == agentId)
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Week)
            .Take(Math.Max(1, max))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<LlmQuotaAdjustment>> GetAdjustmentsAsync(string agentId, ClaimsPrincipal actor, int max = 20, CancellationToken cancellationToken = default)
    {
        Permission.RequireQuotaRead(actor, agentId);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.LlmQuotaAdjustments.AsNoTracking()
            .Where(a => a.AgentId == agentId)
            .OrderByDescending(a => a.CreatedAt)
            .Take(Math.Max(1, max))
            .ToListAsync(cancellationToken);
    }

    public async Task<LlmQuotaStatus> EnsureAvailableAsync(
        ClaimsPrincipal actor, CancellationToken cancellationToken = default, int? boostPercent = null, LlmFeature feature = LlmFeature.Chat)
    {
        Permission.RequireLlmUse(actor, feature);
        var agentId = actor.GetAgentId();
        if (string.IsNullOrEmpty(agentId))
        {
            throw new UnauthorizedAccessException("NOOSEI steht in dieser Rolle nicht zur Verfügung.");
        }

        var status = await StatusAsync(agentId, cancellationToken, boostPercent);
        if (status.IsBlocked)
        {
            var reset = status.NextResetLocal is { } at ? at.ToString("dd.MM.yyyy HH:mm") : "dem nächsten Wochenwechsel";
            throw new LlmQuotaExceededException(
                $"Dein NOOSEI-Kontingent für {status.PeriodLabel} ist aufgebraucht. Neues Kontingent ab {reset} Uhr.");
        }
        // the burn-rate rule reports a runaway agent and stops nothing; this is where it is actually stopped
        if (status.IsDayBlocked)
        {
            throw new LlmQuotaExceededException(
                $"Du hast heute schon {status.ConsumedToday:N0} von {status.DailyLimit:N0} Token verbraucht. "
                + "Die Tagesgrenze schützt dein Wochenkontingent — ab morgen früh geht es weiter.");
        }
        return status;
    }

    public async Task<LlmQuotaCharge> TryChargeAsync(LlmChargeInput input, CancellationToken cancellationToken = default)
    {
        // re-read the clock: a long turn can straddle Monday 00:00, and the row belongs in the week it finished in
        var (year, week) = IsoWeekPeriod.Current();
        // token floor under the reported cost: a route that omits usage.cost must not come out free
        var quotaTokens = LlmQuotaMath.FromCost(
            input.Usage.CostUsd, input.Usage.PromptTokens, input.Usage.CompletionTokens,
            options.Value.PriceFor(input.Model));
        var thresholds = (await configService.GetAsync(cancellationToken)).Anomalies;

        var row = new LlmRequestLog
        {
            AgentId = input.AgentId,
            CreatedAt = DateTime.UtcNow,
            BudgetYear = year,
            BudgetWeek = week,
            Feature = input.Feature,
            Model = input.Model,
            Provider = input.Provider,
            PromptTokens = input.Usage.PromptTokens,
            CompletionTokens = input.Usage.CompletionTokens,
            CachedTokens = input.Usage.CachedPromptTokens,
            ReasoningTokens = input.Usage.ReasoningTokens,
            CostUsd = input.Usage.CostUsd,
            QuotaTokens = quotaTokens,
            DurationMs = input.DurationMs,
            ToolRounds = input.ToolRounds,
            Success = input.Success,
            ErrorMessage = Clip(input.ErrorMessage, 500),
            Prompt = input.Prompt,
            Answer = input.Answer,
            ContextRefsJson = input.ContextRefs is { Count: > 0 } refs ? JsonSerializer.Serialize(refs) : null,
            PromptFingerprint = Fingerprint(input.Prompt),
            FinishReason = Clip(input.Trace?.FinishReason, 32),
            Attempts = input.Trace?.Attempts,
            ModelLatencyMs = input.Trace?.ModelLatencyMs,
            ToolCalls = input.Trace?.ToolCalls,
            ToolFailures = input.Trace?.ToolFailures,
            Degraded = input.Trace?.Degraded,
            Withdrawal = input.Trace?.Withdrawal,
            FailureKind = input.Trace?.FailureKind,
        };

        var persisted = false;
        await using (var db = await dbFactory.CreateDbContextAsync(cancellationToken))
        {
            // R1 is the one rule judged per row: the row is in hand, and a stored flag turns
            // "only anomalies" into an indexed filter instead of a table scan
            if (input.Success && quotaTokens > 0)
            {
                var (mean, count) = await BaselineAsync(db, input.AgentId, thresholds, cancellationToken);
                if (LlmAnomalyEvaluator.IsCostSpike(quotaTokens, mean, count, thresholds))
                {
                    row.IsAnomalous = true;
                    row.AnomalyKind = LlmAnomalyKind.CostSpike;
                }
            }

            db.LlmRequests.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                persisted = true;
            }
            // a failed log row must never swallow the answer the agent is waiting for
            catch (UnauthorizedAccessException) { /* read-only session; nothing to book */ }
            catch (DbUpdateException ex)
            {
                // the call already cost real money; a lost row hides it from quota, anomalies and operations alike
                logger.LogError(ex,
                    "NOOSEI-Anfrage von {Agent} nicht protokolliert: {Tokens} Kontingent-Token, Funktion {Feature}",
                    input.AgentId, quotaTokens, input.Feature);
            }
        }

        var status = await StatusAsync(input.AgentId, cancellationToken);
        return new LlmQuotaCharge(quotaTokens, input.Usage.CostUsd, status, row.AnomalyKind, persisted);
    }

    public async Task SetOverrideAsync(string agentId, long? amount, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
        Permission.RequireWriteAccess(actor);
        if (amount is < 0)
        {
            throw new InvalidOperationException("Das Wochenkontingent darf nicht negativ sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        var previous = agent.LlmQuotaOverride;
        if (previous == amount)
        {
            return;
        }
        agent.LlmQuotaOverride = amount;
        // Agent is not auditable, so log the change against the personnel record explicitly
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agent.Id, AuditAction.Modified, actor,
            ManualAudit.Change("KI-Kontingent",
                previous is null ? "Rang-Standard" : previous.Value.ToString("N0"),
                amount is null ? "Rang-Standard" : amount.Value.ToString("N0"))));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task TopUpAsync(string agentId, long tokens, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
        Permission.RequireWriteAccess(actor);
        if (tokens == 0)
        {
            throw new InvalidOperationException("Die Korrektur muss von null verschieden sein.");
        }
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Bitte eine Begründung für die Korrektur angeben.");
        }

        var (year, week) = IsoWeekPeriod.Current();
        await AddAdjustmentAsync(agentId, year, week, tokens, reason.Trim(), actor, cancellationToken);
    }

    public async Task ResetAsync(string agentId, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
        Permission.RequireWriteAccess(actor);
        if (string.IsNullOrWhiteSpace(reason))
        {
            throw new InvalidOperationException("Bitte eine Begründung für die Zurücksetzung angeben.");
        }

        var (year, week) = IsoWeekPeriod.Current();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var consumed = await ConsumedAsync(db, agentId, year, week, cancellationToken);
        if (consumed <= 0)
        {
            return;
        }
        // book back exactly what is consumed, so the week starts over while the carry-in stays untouched
        await AddAdjustmentAsync(agentId, year, week, consumed, reason.Trim(), actor, cancellationToken);
    }

    /// <summary>Rolling average this request is judged against: the agent's own recent charges, or — for a
    /// newcomer with no history — everyone's, so a first monster request still trips.</summary>
    private static async Task<(double Mean, int Count)> BaselineAsync(
        AppDbContext db, string agentId, LlmAnomalyThresholds thresholds, CancellationToken cancellationToken)
    {
        var since = DateTime.UtcNow.AddDays(-Math.Max(1, thresholds.SpikeBaselineDays));
        var own = await db.LlmRequests.AsNoTracking()
            .Where(r => r.AgentId == agentId && r.CreatedAt >= since && r.Success && r.QuotaTokens > 0)
            .Select(r => r.QuotaTokens)
            .ToListAsync(cancellationToken);
        if (own.Count >= thresholds.SpikeMinBaselineCount)
        {
            return (own.Average(t => (double)t), own.Count);
        }
        if (!thresholds.SpikeUseGlobalFallback)
        {
            return (0, own.Count);
        }

        var global = await db.LlmRequests.AsNoTracking()
            .Where(r => r.CreatedAt >= since && r.Success && r.QuotaTokens > 0)
            .Select(r => r.QuotaTokens)
            .ToListAsync(cancellationToken);
        return global.Count == 0 ? (0, 0) : (global.Average(t => (double)t), global.Count);
    }

    // ---- quota engine ----

    private async Task AddAdjustmentAsync(
        string agentId, int year, int week, long tokens, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.Users.AnyAsync(a => a.Id == agentId, cancellationToken);
        if (!exists)
        {
            throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        }

        db.LlmQuotaAdjustments.Add(new LlmQuotaAdjustment
        {
            AgentId = agentId,
            Year = year,
            Week = week,
            Tokens = tokens,
            Reason = reason,
            CreatedAt = DateTime.UtcNow,
            CreatedById = actor.GetAgentId(),
            CreatedByName = actor.GetCodename(),
        });
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agentId, AuditAction.Modified, actor,
            ManualAudit.Change("KI-Kontingent-Korrektur", null,
                $"{(tokens > 0 ? "+" : string.Empty)}{tokens:N0} Token · {reason}")));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Active partner accounts whose agency has the law chat switched on.</summary>
    private static async Task<List<Agent>> PartnerSpendersAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var agencies = (await db.PartnerAgencyProfiles.AsNoTracking()
                .Select(p => new { p.Agency, p.Features })
                .ToListAsync(cancellationToken))
            .Where(p => p.Features.HasFlag(PartnerFeature.Noosei))
            .Select(p => (PartnerAgency?)p.Agency)
            .ToList();
        if (agencies.Count == 0)
        {
            return [];
        }
        return await db.Users.AsNoTracking()
            .Where(u => u.Status == AgentStatus.Active && u.PartnerAgency != null && agencies.Contains(u.PartnerAgency))
            .ToListAsync(cancellationToken);
    }

    private static async Task<LlmQuotaStatus> BuildStatusAsync(
        AppDbContext db, Agent agent, LlmQuotaConfig config, int boostPercent, QuotaSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var (year, week) = IsoWeekPeriod.Current();
        var rules = config.For(agent.Rank, agent.PartnerAgency);
        // the boost sits on top of an individual override too: it says what the active upstream costs, not who
        // is allowed how much, and those are two different questions
        var rawBaseWeekly = agent.LlmQuotaOverride ?? rules.BaseWeekly;
        var baseWeekly = LlmQuotaMath.Boosted(rawBaseWeekly, boostPercent);
        // elapsed weeks close on the RAW base, never on today's boost. Closing is lazy - it happens on the first
        // read of the new week - so the surcharge in force at that moment is the one the switch left behind, not
        // the one the week actually ran under. Feeding it in let a week spent entirely on the cheaper upstream
        // hand over a multiple of the carry it earned. The plain base can only under-grant, which costs nothing.
        // Exact would mean recording the boost on the period row; that needs a column and a migration.
        var carryIn = await CloseElapsedAsync(db, agent, snapshot, rawBaseWeekly, rules.CarryOverPercent, year, week, cancellationToken);
        return new LlmQuotaStatus(agent.Id, agent.Codename, agent.Rank, year, week,
            baseWeekly, rawBaseWeekly, boostPercent, carryIn, snapshot.Consumed(agent.Id, year, week), rules.CarryOverPercent,
            agent.LlmQuotaOverride is not null,
            // measured against the base, so an individual override moves the daily ceiling with it
            LlmQuotaMath.DailyLimit(baseWeekly, rules.DailyPercent), snapshot.ConsumedToday(agent.Id));
    }

    /// <summary>Everything the weekly ledger reads, for one agent or for a whole roster, in four queries either way.</summary>
    /// <remarks>
    /// The per-agent path asked seven questions — a hundred-agent roster is seven hundred round trips inside one
    /// synchronous render, and the same path runs behind every single answer, because a charge closes by reading
    /// the status back. Grouping is safe here because none of the four reads depends on another.
    /// </remarks>
    private sealed class QuotaSnapshot
    {
        private static readonly Dictionary<LlmQuotaLedger.WeekKey, long> NoWeeks = new();

        private readonly Dictionary<string, Dictionary<LlmQuotaLedger.WeekKey, long>> _closed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Dictionary<LlmQuotaLedger.WeekKey, long>> _consumed = new(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _today = new(StringComparer.Ordinal);

        public static async Task<QuotaSnapshot> LoadAsync(
            AppDbContext db, IReadOnlyList<string> agentIds, CancellationToken cancellationToken)
        {
            var snapshot = new QuotaSnapshot();
            if (agentIds.Count == 0)
            {
                return snapshot;
            }
            var ids = agentIds.ToList();
            // from local midnight: the week already rolls over at local Monday 00:00, and two clocks would let a
            // day end before or after the week it belongs to
            var dayStartUtc = DateTime.Now.Date.ToUniversalTime();

            var closed = await db.LlmQuotaPeriods.AsNoTracking()
                .Where(p => ids.Contains(p.AgentId))
                .Select(p => new { p.AgentId, p.Year, p.Week, p.CarryOut })
                .ToListAsync(cancellationToken);
            foreach (var row in closed)
            {
                Slot(snapshot._closed, row.AgentId)[new LlmQuotaLedger.WeekKey(row.Year, row.Week)] = row.CarryOut;
            }

            var charged = await db.LlmRequests.AsNoTracking()
                .Where(r => ids.Contains(r.AgentId))
                .GroupBy(r => new { r.AgentId, r.BudgetYear, r.BudgetWeek })
                .Select(g => new { g.Key.AgentId, g.Key.BudgetYear, g.Key.BudgetWeek, Sum = g.Sum(x => x.QuotaTokens) })
                .ToListAsync(cancellationToken);
            foreach (var row in charged)
            {
                Slot(snapshot._consumed, row.AgentId)[new LlmQuotaLedger.WeekKey(row.BudgetYear, row.BudgetWeek)] = row.Sum;
            }

            var corrected = await db.LlmQuotaAdjustments.AsNoTracking()
                .Where(a => ids.Contains(a.AgentId))
                .GroupBy(a => new { a.AgentId, a.Year, a.Week })
                .Select(g => new { g.Key.AgentId, g.Key.Year, g.Key.Week, Sum = g.Sum(x => x.Tokens) })
                .ToListAsync(cancellationToken);
            foreach (var row in corrected)
            {
                var weeks = Slot(snapshot._consumed, row.AgentId);
                var key = new LlmQuotaLedger.WeekKey(row.Year, row.Week);
                weeks[key] = weeks.GetValueOrDefault(key) - row.Sum;
            }

            var today = await db.LlmRequests.AsNoTracking()
                .Where(r => ids.Contains(r.AgentId) && r.CreatedAt >= dayStartUtc)
                .GroupBy(r => r.AgentId)
                .Select(g => new { AgentId = g.Key, Sum = g.Sum(x => x.QuotaTokens) })
                .ToListAsync(cancellationToken);
            foreach (var row in today)
            {
                snapshot._today[row.AgentId] = row.Sum;
            }

            return snapshot;
        }

        /// <summary>Latest week the agent has a closed period for, and what it handed on.</summary>
        public (LlmQuotaLedger.WeekKey Key, long CarryOut)? LatestClosed(string agentId)
        {
            if (!_closed.TryGetValue(agentId, out var weeks))
            {
                return null;
            }
            (LlmQuotaLedger.WeekKey Key, long CarryOut)? best = null;
            foreach (var (key, carryOut) in weeks)
            {
                if (best is not { } current || IsoWeekPeriod.IsBefore(current.Key.Year, current.Key.Week, key.Year, key.Week))
                {
                    best = (key, carryOut);
                }
            }
            return best;
        }

        /// <summary>Carry-over a specific closed week handed on; 0 when that week was never closed.</summary>
        public long CarryOutOf(string agentId, int year, int week)
            => _closed.TryGetValue(agentId, out var weeks)
                && weeks.TryGetValue(new LlmQuotaLedger.WeekKey(year, week), out var carry)
                    ? carry
                    : 0L;

        /// <summary>Net consumption per week: what the requests charged, minus what was corrected back.</summary>
        public IReadOnlyDictionary<LlmQuotaLedger.WeekKey, long> ConsumedByWeek(string agentId)
            => _consumed.TryGetValue(agentId, out var weeks) ? weeks : NoWeeks;

        public long Consumed(string agentId, int year, int week)
            => ConsumedByWeek(agentId).TryGetValue(new LlmQuotaLedger.WeekKey(year, week), out var used) ? used : 0L;

        public long ConsumedToday(string agentId) => _today.GetValueOrDefault(agentId);

        /// <summary>Earliest week carrying a charge or a correction; weeks before it had nothing to spend, so they
        /// must not manufacture carry-over out of an untouched quota.</summary>
        public LlmQuotaLedger.WeekKey? FirstCharged(string agentId)
        {
            LlmQuotaLedger.WeekKey? first = null;
            foreach (var key in ConsumedByWeek(agentId).Keys)
            {
                if (first is not { } current || IsoWeekPeriod.IsBefore(key.Year, key.Week, current.Year, current.Week))
                {
                    first = key;
                }
            }
            return first;
        }

        private static Dictionary<LlmQuotaLedger.WeekKey, long> Slot(
            Dictionary<string, Dictionary<LlmQuotaLedger.WeekKey, long>> map, string agentId)
        {
            if (!map.TryGetValue(agentId, out var weeks))
            {
                weeks = new Dictionary<LlmQuotaLedger.WeekKey, long>();
                map[agentId] = weeks;
            }
            return weeks;
        }
    }

    /// <summary>Net consumption of one week: what the requests charged, minus what was corrected back.</summary>
    private static async Task<long> ConsumedAsync(AppDbContext db, string agentId, int year, int week, CancellationToken cancellationToken)
    {
        var charged = await db.LlmRequests.AsNoTracking()
            .Where(r => r.AgentId == agentId && r.BudgetYear == year && r.BudgetWeek == week)
            .SumAsync(r => (long?)r.QuotaTokens, cancellationToken) ?? 0L;
        var corrected = await db.LlmQuotaAdjustments.AsNoTracking()
            .Where(a => a.AgentId == agentId && a.Year == year && a.Week == week)
            .SumAsync(a => (long?)a.Tokens, cancellationToken) ?? 0L;
        return charged - corrected;
    }

    /// <summary>Writes a period row for every elapsed week and returns the carry-over the running week may use.</summary>
    /// <remarks>Reads nothing itself — every input comes from the snapshot, including the anomaly branch, so this
    /// stays free of round trips whether it runs for one agent or for thirty.</remarks>
    private static async Task<long> CloseElapsedAsync(
        AppDbContext db, Agent agent, QuotaSnapshot snapshot, long baseWeekly, int carryPercent,
        int currentYear, int currentWeek, CancellationToken cancellationToken)
    {
        var latest = snapshot.LatestClosed(agent.Id);
        var plan = LlmQuotaLedger.Backfill(
            latest?.Key,
            latest?.CarryOut ?? 0L,
            snapshot.FirstCharged(agent.Id),
            snapshot.ConsumedByWeek(agent.Id),
            baseWeekly,
            carryPercent,
            currentYear,
            currentWeek);

        if (plan.Outcome == BackfillOutcome.ReadPredecessor)
        {
            // the running week (or later) is already closed — anomaly; read the direct predecessor instead
            var (priorYear, priorWeek) = IsoWeekPeriod.Previous(currentYear, currentWeek);
            return LlmQuotaMath.ClampCarryIn(
                snapshot.CarryOutOf(agent.Id, priorYear, priorWeek), baseWeekly, carryPercent);
        }

        var persist = true;
        foreach (var draft in plan.ToClose)
        {
            if (!persist)
            {
                continue;
            }
            persist = await TryCloseAsync(db, agent, draft, cancellationToken);
        }
        return plan.CarryIn;
    }

    private static async Task<bool> TryCloseAsync(
        AppDbContext db, Agent agent, LlmQuotaLedger.PeriodDraft draft, CancellationToken cancellationToken)
    {
        var row = new LlmQuotaPeriod
        {
            AgentId = agent.Id,
            Year = draft.Year,
            Week = draft.Week,
            BaseWeekly = draft.BaseWeekly,
            CarryIn = draft.CarryIn,
            Consumed = draft.Consumed,
            CarryOut = draft.CarryOut,
            CarryPercent = draft.CarryPercent,
            RankAtClose = agent.Rank,
            ClosedAt = DateTime.UtcNow,
        };
        db.LlmQuotaPeriods.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // a pure read by read-only supervision or a demo visitor triggered this; compute without persisting
            db.Entry(row).State = EntityState.Detached;
            return false;
        }
        catch (DbUpdateException)
        {
            // the unique index on (Agent, Jahr, Woche) caught a concurrent close; its numbers stand
            db.Entry(row).State = EntityState.Detached;
            return true;
        }
    }

    // ---- helpers ----

    private static string? Clip(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : text.Length <= max ? text : text[..max];

    /// <summary>Normalised prompt hash; the first, cheap pass of the near-identical-prompt rule.</summary>
    public static string? Fingerprint(string? prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return null;
        }
        var sb = new StringBuilder(Math.Min(prompt.Length, 512));
        var space = false;
        foreach (var c in prompt)
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
                space = false;
            }
            else if (!space)
            {
                sb.Append(' ');
                space = true;
            }
            if (sb.Length >= 512)
            {
                break;
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString().Trim())));
    }
}

/// <summary>The actor is out of NOOSEI tokens for this week. Typed so the UI can catch it specifically.</summary>
public sealed class LlmQuotaExceededException(string message) : InvalidOperationException(message);
