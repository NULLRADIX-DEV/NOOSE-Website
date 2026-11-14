using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Account;

/// <summary>Writes the NOOSE-specific claims into the identity cookie at login so policies and UI decide from claims without per-request DB hits.</summary>
public class AgentClaimsPrincipalFactory(
    UserManager<Agent> userManager,
    RoleManager<IdentityRole> roleManager,
    IOptions<IdentityOptions> optionsAccessor,
    IConfiguration configuration)
    : UserClaimsPrincipalFactory<Agent, IdentityRole>(userManager, roleManager, optionsAccessor)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(Agent user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        identity.AddClaim(new Claim(AgentClaimTypes.Codename, user.Codename ?? string.Empty));
        identity.AddClaim(new Claim(AgentClaimTypes.BadgeNumber, user.BadgeNumber ?? string.Empty));
        identity.AddClaim(new Claim(AgentClaimTypes.Status, user.Status.ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.IsAdmin, user.IsAdmin ? "true" : "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsBootstrap, BootstrapAdmins.Contains(configuration, user.DiscordId) ? "true" : "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsAiOwner, AiOwners.Contains(configuration, user.DiscordId) ? "true" : "false"));
        // TRU follows the division, so a row from before the division column reads the same
        var unit = DivisionRules.Of(user);
        identity.AddClaim(new Claim(AgentClaimTypes.IsTRU, DivisionRules.IsTru(unit) ? "true" : "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsHRB, user.IsHRB ? "true" : "false"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsTeamLead, user.IsTeamLead ? "true" : "false"));

        if (unit.Home is { } home)
        {
            identity.AddClaim(new Claim(AgentClaimTypes.Division, ((int)home).ToString()));
        }

        if (user.Rank is not null)
        {
            identity.AddClaim(new Claim(AgentClaimTypes.Rank, ((int)user.Rank.Value).ToString()));
        }

        if (user.PartnerAgency is { } agency)
        {
            identity.AddClaim(new Claim(AgentClaimTypes.PartnerAgency, ((int)agency).ToString()));
        }

        if (user.PartnerAgency is not null && user.PartnerUnitId is { } partnerUnit)
        {
            identity.AddClaim(new Claim(AgentClaimTypes.PartnerUnit, partnerUnit));
            identity.AddClaim(new Claim(AgentClaimTypes.IsPartnerUnitLead, user.IsPartnerUnitLead ? "true" : "false"));
        }

        return identity;
    }
}
