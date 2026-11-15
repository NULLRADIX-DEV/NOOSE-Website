using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Units inside the partner agencies: listing, admin editing, visibility allowlists.</summary>
public interface IPartnerUnitService
{
    /// <summary>All units (cached 10 s), ordered by agency and sort order.</summary>
    Task<IReadOnlyList<PartnerUnitView>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>One unit, or null.</summary>
    Task<PartnerUnitView?> GetAsync(string? id, CancellationToken cancellationToken = default);

    /// <summary>Units of one agency.</summary>
    Task<IReadOnlyList<PartnerUnitView>> ForAgencyAsync(PartnerAgency agency, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a unit. Admin only.</summary>
    Task<string> SaveAsync(PartnerUnitInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes a unit without active accounts. Admin only.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Release rules of one unit (records of a type it sees automatically).</summary>
    Task<IReadOnlyList<PartnerRuleView>> GetRulesAsync(string unitId, CancellationToken cancellationToken = default);

    /// <summary>Replaces the release rules of one unit. Admin only.</summary>
    Task SaveRulesAsync(string unitId, IReadOnlyList<PartnerRuleView> rules, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Saves (or, when null, clears) a unit's record-type and tab allowlist. Admin only.</summary>
    Task SaveVisibilityAsync(string id, PartnerUnitVisibility? visibility, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
