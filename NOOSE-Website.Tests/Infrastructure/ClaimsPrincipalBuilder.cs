using System.Globalization;
using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Tests.Infrastructure;

/// <summary>Fluent builder for a claims principal carrying NOOSE agent claims.</summary>
public sealed class ClaimsPrincipalBuilder
{
    private readonly List<Claim> _claims = new();

    public static ClaimsPrincipalBuilder Agent(string id = "agent-1")
        => new ClaimsPrincipalBuilder().NameId(id).WithStatus(AgentStatus.Active);

    /// <summary>An unauthenticated principal with no identity.</summary>
    public static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    public ClaimsPrincipalBuilder NameId(string id)
        => Set(ClaimTypes.NameIdentifier, id);

    public ClaimsPrincipalBuilder WithRank(Rank rank)
        => Set(AgentClaimTypes.Rank, ((int)rank).ToString(CultureInfo.InvariantCulture));

    public ClaimsPrincipalBuilder WithStatus(AgentStatus status)
        => Set(AgentClaimTypes.Status, status.ToString());

    public ClaimsPrincipalBuilder WithCodename(string codename)
        => Set(AgentClaimTypes.Codename, codename);

    public ClaimsPrincipalBuilder WithBadge(string badge)
        => Set(AgentClaimTypes.BadgeNumber, badge);

    public ClaimsPrincipalBuilder AsAdmin(bool value = true) => Flag(AgentClaimTypes.IsAdmin, value);
    public ClaimsPrincipalBuilder AsBootstrap(bool value = true) => Flag(AgentClaimTypes.IsBootstrap, value);
    public ClaimsPrincipalBuilder AsAiOwner(bool value = true) => Flag(AgentClaimTypes.IsAiOwner, value);
    public ClaimsPrincipalBuilder AsTru(bool value = true) => Flag(AgentClaimTypes.IsTRU, value);
    public ClaimsPrincipalBuilder AsHrb(bool value = true) => Flag(AgentClaimTypes.IsHRB, value);

    /// <summary>Home division claim; does not touch the TRU claim, so pair it with <see cref="AsTru"/> for an HRT member.</summary>
    public ClaimsPrincipalBuilder AsDivision(Division division)
        => Set(AgentClaimTypes.Division, ((int)division).ToString(CultureInfo.InvariantCulture));
    public ClaimsPrincipalBuilder AsTeamLead(bool value = true) => Flag(AgentClaimTypes.IsTeamLead, value);
    public ClaimsPrincipalBuilder AsDemo(bool value = true) => Flag(AgentClaimTypes.IsDemo, value);

    public ClaimsPrincipalBuilder AsPartner(PartnerAgency agency, PartnerRank rank)
    {
        Set(AgentClaimTypes.PartnerAgency, ((int)agency).ToString(CultureInfo.InvariantCulture));
        return Set(AgentClaimTypes.PartnerRank, ((int)rank).ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Write a raw claim value verbatim (for boundary cases like malformed claims).</summary>
    public ClaimsPrincipalBuilder Raw(string type, string value)
        => Set(type, value);

    public ClaimsPrincipal Build()
        => new(new ClaimsIdentity(_claims, authenticationType: "Test"));

    public static implicit operator ClaimsPrincipal(ClaimsPrincipalBuilder b) => b.Build();

    private ClaimsPrincipalBuilder Flag(string type, bool value)
        => value ? Set(type, "true") : this;

    private ClaimsPrincipalBuilder Set(string type, string value)
    {
        _claims.RemoveAll(c => c.Type == type);
        _claims.Add(new Claim(type, value));
        return this;
    }
}
