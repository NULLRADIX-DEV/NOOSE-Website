using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFoodLimitService" />
public class FoodLimitService(
    IDbContextFactory<AppDbContext> dbFactory,
    IFoodLimitConfigService configService) : IFoodLimitService
{
    public async Task<FoodLimitStatus> GetStatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var config = await configService.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        var week = FoodWeek.Current();
        var consumed = await ConsumedAsync(db, [agentId], week, cancellationToken);
        return Build(agent, config, week, consumed.GetValueOrDefault(agentId));
    }

    public async Task<IReadOnlyList<FoodLimitStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default)
    {
        var config = await configService.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agents = await db.Users.AsNoTracking().OnlySelectable()
            .OrderBy(a => a.Codename)
            .ToListAsync(cancellationToken);
        var week = FoodWeek.Current();
        var consumed = await ConsumedAsync(db, agents.Select(a => a.Id).ToList(), week, cancellationToken);
        return agents.Select(a => Build(a, config, week, consumed.GetValueOrDefault(a.Id))).ToList();
    }

    public async Task SetOverrideAsync(string agentId, int? food, int? drink, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
        if (food is < 0 || drink is < 0)
        {
            throw new InvalidOperationException("Die Wochenlimits dürfen nicht negativ sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var agent = await db.Users.FirstOrDefaultAsync(a => a.Id == agentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent '{agentId}' nicht gefunden.");
        var previousFood = agent.FoodLimitOverride;
        var previousDrink = agent.DrinkLimitOverride;
        if (previousFood == food && previousDrink == drink)
        {
            return;
        }
        agent.FoodLimitOverride = food;
        agent.DrinkLimitOverride = drink;
        // Agent is not auditable
        db.AuditLogs.Add(ManualAudit.Row(nameof(Agent), agent.Id, AuditAction.Modified, actor,
            new Dictionary<string, object?[]>
            {
                ["Verpflegungslimit Essen"] = new object?[] { Label(previousFood), Label(food) },
                ["Verpflegungslimit Trinken"] = new object?[] { Label(previousDrink), Label(drink) },
            }));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static string Label(int? value) => value is null ? "Rang-Standard" : $"{value} pro Woche";

    private static FoodLimitStatus Build(Agent agent, FoodLimitConfig config, DateTime week, (int Food, int Drink) consumed)
    {
        var rules = config.For(agent.Rank, DivisionRules.Of(agent).Home);
        return new FoodLimitStatus(agent.Id, agent.Codename, agent.Rank, week,
            agent.FoodLimitOverride ?? rules.FoodPerWeek,
            agent.DrinkLimitOverride ?? rules.DrinkPerWeek,
            consumed.Food, consumed.Drink,
            agent.FoodLimitOverride is not null, agent.DrinkLimitOverride is not null);
    }

    /// <summary>Approved pieces charged to one week per agent; rejected, withdrawn and deleted requests never count.</summary>
    internal static async Task<Dictionary<string, (int Food, int Drink)>> ConsumedAsync(AppDbContext db,
        IReadOnlyCollection<string> agentIds, DateTime week, CancellationToken cancellationToken)
    {
        if (agentIds.Count == 0)
        {
            return new();
        }
        var rows = await db.FoodRequests.AsNoTracking()
            .Where(r => r.AgentId != null && agentIds.Contains(r.AgentId)
                && r.LimitWeekStart == week
                && (r.Status == FoodRequestStatus.Approved || r.Status == FoodRequestStatus.Issued))
            .Select(r => new { r.AgentId, Food = r.FoodApproved ?? 0, Drink = r.DrinkApproved ?? 0 })
            .ToListAsync(cancellationToken);
        return rows
            .GroupBy(r => r.AgentId!)
            .ToDictionary(g => g.Key, g => (g.Sum(r => r.Food), g.Sum(r => r.Drink)));
    }
}
