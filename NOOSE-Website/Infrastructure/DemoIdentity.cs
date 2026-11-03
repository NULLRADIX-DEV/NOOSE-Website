using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Infrastructure;

/// <summary>The synthetic read-only visitor presented to anonymous users while demo mode is active.</summary>
public static class DemoIdentity
{
    /// <summary>Stable id of the seeded demo agent row; also the principal's NameIdentifier.</summary>
    public const string AgentId = "demo-agent";

    /// <summary>Auth type marker so the identity counts as authenticated.</summary>
    public const string AuthenticationType = "Demo";

    public const string Codename = "Demo";

    /// <summary>Director (Board, home CID, so no TRU) + HRB for full read access; not admin; marked read-only via IsDemo.</summary>
    public static ClaimsPrincipal BuildPrincipal()
    {
        var identity = new ClaimsIdentity(AuthenticationType);
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, AgentId));
        identity.AddClaim(new Claim(AgentClaimTypes.Codename, Codename));
        identity.AddClaim(new Claim(AgentClaimTypes.Status, AgentStatus.Active.ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Rank, ((int)Rank.Director).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Division, ((int)Division.Cid).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.IsTRU, "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsHRB, "true"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsAdmin, "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsTeamLead, "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsDemo, "true"));
        return new ClaimsPrincipal(identity);
    }
}
