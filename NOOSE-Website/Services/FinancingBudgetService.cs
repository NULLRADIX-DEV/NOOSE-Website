using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFinancingBudgetService" />
public class FinancingBudgetService(
    IDbContextFactory<AppDbContext> dbFactory,
    IFinancingConfigService configService) : IFinancingBudgetService
{
    /// <summary>Upper bound for the lazy backfill; a chain older than this cannot reach the running month anyway.</summary>
    private const int MaxBackfillMonths = 24;

    public async Task<FinancingBudgetStatus> GetStatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var config = await configService.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        return await BuildStatusAsync(db, agent, config, cancellationToken);
    }

    public async Task<IReadOnlyList<FinancingBudgetStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await configService.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Users.AsNoTracking().OnlySelectable()
            .OrderBy(a => a.Codename)
            .ToListAsync(cancellationToken);

        var list = new List<FinancingBudgetStatus>(agents.Count);
        foreach (var agent in agents)
        {
            list.Add(await BuildStatusAsync(db, agent, config, cancellationToken));
        }
        return list;
    }

    public async Task<List<FinancingBudgetPeriod>> GetPeriodsAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.FinancingBudgetPeriods.AsNoTracking()
            .Where(p => p.AgentId == agentId)
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .ToListAsync(cancellationToken);
    }

    public async Task SetOverrideAsync(string agentId, decimal? amount, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
        if (amount is < 0)
        {
            throw new InvalidOperationException("Das Monatsbudget darf nicht negativ sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        var previous = agent.FinancingBudgetOverride;
        if (previous == amount)
        {
            return;
        }
        agent.FinancingBudgetOverride = amount;
        // Agent is not auditable, so log the change against the personnel record explicitly
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agent.Id, AuditAction.Modified, actor,
            ManualAudit.Change("Finanzierungsbudget",
                previous is null ? "Rang-Standard" : Money.Format(previous.Value),
                amount is null ? "Rang-Standard" : Money.Format(amount.Value))));
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- budget engine ----

    private static async Task<FinancingBudgetStatus> BuildStatusAsync(
        AppDbContext db, Agent agent, FinancingBudgetConfig config, CancellationToken cancellationToken)
    {
        var (year, month) = FinancingPeriod.Current();
        var rules = config.For(agent.Rank, DivisionRules.Of(agent).Home);
        var baseBudget = agent.FinancingBudgetOverride ?? rules.BaseMonthly;
        var carryIn = await CloseElapsedAsync(db, agent, baseBudget, rules.CarryOverPercent, year, month, cancellationToken);
        var consumed = await ConsumedAsync(db, agent.Id, year, month, cancellationToken);
        return new FinancingBudgetStatus(agent.Id, agent.Codename, agent.Rank, year, month,
            baseBudget, carryIn, consumed, rules.CarryOverPercent, agent.FinancingBudgetOverride is not null);
    }

    /// <summary>Approved subsidy charged to one budget month; rejected, withdrawn and deleted requests never count.</summary>
    private static async Task<decimal> ConsumedAsync(AppDbContext db, string agentId, int year, int month, CancellationToken cancellationToken)
        => await db.FinancingRequests.AsNoTracking()
            .Where(r => r.AgentId == agentId
                && r.BudgetYear == year && r.BudgetMonth == month
                && (r.Status == FinancingStatus.Approved || r.Status == FinancingStatus.Paid))
            .SumAsync(r => r.ApprovedSubsidy ?? 0m, cancellationToken);

    /// <summary>Writes a period row for every elapsed month and returns the carry-over the running month may use.</summary>
    /// <remarks>
    /// Accepted limitation: an elapsed month is closed with the base budget and carry percentage passed in,
    /// i.e. the ones in force at the moment of this call, not the ones the month itself was governed by.
    /// A promotion, an individual override or a rule change made between the month's end and the first
    /// budget read afterwards therefore lands in that month and is then frozen. The window stays small in
    /// practice: every approval and every visit to the budget overview closes the elapsed months, and the
    /// overview closes all active agents in one go.
    /// </remarks>
    private static async Task<decimal> CloseElapsedAsync(AppDbContext db, Agent agent, decimal baseBudget, int carryPercent,
        int currentYear, int currentMonth, CancellationToken cancellationToken)
    {
        var latest = await db.FinancingBudgetPeriods.AsNoTracking()
            .Where(p => p.AgentId == agent.Id)
            .OrderByDescending(p => p.Year).ThenByDescending(p => p.Month)
            .Select(p => new { p.Year, p.Month, p.CarryOut })
            .FirstOrDefaultAsync(cancellationToken);

        // the running month (or later) is already closed — anomaly; read the direct predecessor instead
        if (latest is not null && !FinancingPeriod.IsBefore(latest.Year, latest.Month, currentYear, currentMonth))
        {
            var (priorYear, priorMonth) = FinancingPeriod.Previous(currentYear, currentMonth);
            return await db.FinancingBudgetPeriods.AsNoTracking()
                .Where(p => p.AgentId == agent.Id && p.Year == priorYear && p.Month == priorMonth)
                .Select(p => (decimal?)p.CarryOut)
                .FirstOrDefaultAsync(cancellationToken) ?? 0m;
        }

        int year, month;
        decimal carry;
        if (latest is not null)
        {
            (year, month) = FinancingPeriod.Next(latest.Year, latest.Month);
            carry = latest.CarryOut;
        }
        else
        {
            // start at the earliest month that actually carries a reservation: months before that had
            // nothing to spend, so they must not manufacture carry-over out of an untouched budget
            var first = await db.FinancingRequests.AsNoTracking()
                .Where(r => r.AgentId == agent.Id && r.BudgetYear != null && r.BudgetMonth != null)
                .OrderBy(r => r.BudgetYear).ThenBy(r => r.BudgetMonth)
                .Select(r => new { Year = r.BudgetYear!.Value, Month = r.BudgetMonth!.Value })
                .FirstOrDefaultAsync(cancellationToken);
            if (first is null)
            {
                return 0m;
            }
            (year, month) = (first.Year, first.Month);
            carry = 0m;
        }

        var persist = true;
        var guard = 0;
        while (FinancingPeriod.IsBefore(year, month, currentYear, currentMonth) && guard++ < MaxBackfillMonths)
        {
            var consumed = await ConsumedAsync(db, agent.Id, year, month, cancellationToken);
            var carryOut = FinancingMath.CarryOut(baseBudget + carry - consumed, carryPercent);
            if (persist)
            {
                var (stored, persisted) = await TryCloseAsync(db, agent, year, month,
                    baseBudget, carry, consumed, carryOut, carryPercent, cancellationToken);
                carry = stored;
                persist = persisted;
            }
            else
            {
                carry = carryOut;
            }
            (year, month) = FinancingPeriod.Next(year, month);
        }

        // carry may only come from the DIRECT predecessor; a backfill cut short by the guard hands over nothing
        return year == currentYear && month == currentMonth ? carry : 0m;
    }

    private static async Task<(decimal Carry, bool Persisted)> TryCloseAsync(AppDbContext db, Agent agent, int year, int month,
        decimal baseBudget, decimal carryIn, decimal consumed, decimal carryOut, int carryPercent, CancellationToken cancellationToken)
    {
        var row = new FinancingBudgetPeriod
        {
            AgentId = agent.Id,
            Year = year,
            Month = month,
            BaseBudget = baseBudget,
            CarryIn = carryIn,
            Consumed = consumed,
            CarryOut = carryOut,
            CarryPercent = carryPercent,
            RankAtClose = agent.Rank,
            ClosedAt = DateTime.UtcNow,
        };
        db.FinancingBudgetPeriods.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return (carryOut, true);
        }
        catch (UnauthorizedAccessException)
        {
            // a pure read by read-only supervision or a demo visitor triggered this; compute without persisting
            db.Entry(row).State = EntityState.Detached;
            return (carryOut, false);
        }
        catch (DbUpdateException)
        {
            // the unique index on (Agent, Jahr, Monat) caught a concurrent close: adopt its numbers
            db.Entry(row).State = EntityState.Detached;
            var stored = await db.FinancingBudgetPeriods.AsNoTracking()
                .Where(p => p.AgentId == agent.Id && p.Year == year && p.Month == month)
                .Select(p => (decimal?)p.CarryOut)
                .FirstOrDefaultAsync(cancellationToken);
            return (stored ?? carryOut, true);
        }
    }
}
