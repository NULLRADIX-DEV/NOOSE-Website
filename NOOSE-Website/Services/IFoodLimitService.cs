using System.Security.Claims;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <summary>Per-agent weekly food and drink allowance: rank default or individual value, minus what is already approved this week.</summary>
public interface IFoodLimitService
{
    /// <summary>Allowance of one agent for the running week.</summary>
    Task<FoodLimitStatus> GetStatusAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Allowance of every active internal agent, for the leadership overview.</summary>
    Task<IReadOnlyList<FoodLimitStatus>> GetAllStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Sets or clears (null) an agent's individual weekly food and drink limits.</summary>
    Task SetOverrideAsync(string agentId, int? food, int? drink, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
