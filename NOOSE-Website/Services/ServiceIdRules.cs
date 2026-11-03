using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Why a service ID card can or cannot be shown to a viewer.</summary>
public enum ServiceIdAccess
{
    Allowed,
    NotFound,
    /// <summary>The viewer may not print this agent's card.</summary>
    Forbidden,
    /// <summary>The agent holds no card: not active, no valid badge number, partner or supervision.</summary>
    NotIssuable,
}

/// <summary>Single source for who holds a service ID card and who may print it.</summary>
/// <remarks>
/// A card needs an active in-house agent with a rank and a badge number from <see cref="BadgeNumbers"/>; the number is
/// also what the public check looks up. The agent prints their own, leadership and the Administration Division
/// print anyone's. Read-only supervision, partners and the demo visitor never print.
/// </remarks>
public static class ServiceIdRules
{
    /// <summary>The canonical badge number the card carries, or null when the agent has no valid one.</summary>
    public static string? Number(Agent agent)
    {
        var number = BadgeNumbers.Normalize(agent.BadgeNumber);
        return number is not null && BadgeNumbers.All.Contains(number, StringComparer.Ordinal) ? number : null;
    }

    /// <summary>True when the agent holds a card at all, whoever asks.</summary>
    public static bool IsHolder(Agent agent)
        => agent.Status == AgentStatus.Active
           && agent.PartnerAgency is null
           && !agent.IsTeamLead
           && agent.Rank is not null
           && !string.IsNullOrWhiteSpace(agent.Codename)
           && Number(agent) is not null;

    /// <summary>Viewer side only: may this principal print the card of the agent with this id.</summary>
    public static bool MayPrint(ClaimsPrincipal viewer, string targetAgentId)
    {
        if (!viewer.IsInternalAgent() || viewer.IsTeamLead() || viewer.IsDemo())
        {
            return false;
        }
        return string.Equals(viewer.GetAgentId(), targetAgentId, StringComparison.Ordinal)
            || viewer.IsHrbOrLeadership();
    }

    /// <summary>Full decision for one viewer and one agent.</summary>
    public static ServiceIdAccess Check(ClaimsPrincipal viewer, Agent? target)
    {
        if (target is null)
        {
            return ServiceIdAccess.NotFound;
        }
        if (!MayPrint(viewer, target.Id))
        {
            return ServiceIdAccess.Forbidden;
        }
        return IsHolder(target) ? ServiceIdAccess.Allowed : ServiceIdAccess.NotIssuable;
    }
}
