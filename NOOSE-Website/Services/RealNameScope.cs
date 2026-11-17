using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Whose real name a viewer may read. HRT and CTD members stay codename-only below leadership; the one place for that rule.</summary>
/// <param name="Regular">Viewer may read real names at all (<see cref="AgentPrincipalExtensions.MayRealNameSee" />).</param>
/// <param name="Covert">Viewer may also read real names of HRT and CTD members (leadership).</param>
/// <param name="ViewerId">The viewer's own account, whose real name never counts as covert to him.</param>
public readonly record struct RealNameScope(bool Regular, bool Covert, string? ViewerId = null)
{
    /// <summary>No real names at all.</summary>
    public static RealNameScope None => default;

    /// <summary>Scope of a signed-in principal.</summary>
    public static RealNameScope For(ClaimsPrincipal user)
    {
        var regular = user.MayRealNameSee();
        return new RealNameScope(regular, regular && user.IsLeadership(), user.GetAgentId());
    }

    /// <summary>Scope from a viewer snapshot.</summary>
    public static RealNameScope For(ViewerScope view)
        => new(view.MayRealName, view.MayRealName && view.IsLeadership, view.MeId);

    /// <summary>Seated in a division whose members stay codename-only; the Board is not.</summary>
    public static bool IsCovert(OrgUnit unit) => unit is { IsBoard: false, Home: Division.Hrt or Division.Ctd };

    /// <summary>Whether the real name of an account in this unit may be shown.</summary>
    public bool MaySee(string? agentId, OrgUnit unit)
        => Regular && (Covert || !IsCovert(unit) || (agentId is not null && agentId == ViewerId));

    /// <summary>Whether the real name of this agent may be shown.</summary>
    public bool MaySee(Agent agent) => MaySee(agent.Id, DivisionRules.Of(agent));

    /// <summary>The agent's real name when the viewer may read it, else null.</summary>
    public string? Of(Agent? agent)
        => agent is not null && !string.IsNullOrWhiteSpace(agent.RealName) && MaySee(agent) ? agent.RealName : null;

    /// <summary>Real name of an account in a known unit when the viewer may read it, else null.</summary>
    public string? Of(string? agentId, string? realName, OrgUnit unit)
        => !string.IsNullOrWhiteSpace(realName) && MaySee(agentId, unit) ? realName : null;

    /// <summary>Real name from stored columns when the viewer may read it, else null.</summary>
    public string? Of(string? agentId, string? realName, Rank? rank, Division? division, bool isTru, bool isPartner = false)
        => !string.IsNullOrWhiteSpace(realName) && MaySee(agentId, DivisionRules.Of(rank, division, isTru, !isPartner))
            ? realName
            : null;
}
