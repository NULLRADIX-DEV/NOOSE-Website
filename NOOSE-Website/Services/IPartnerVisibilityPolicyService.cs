using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>
/// Per partner-rank default visibility of record types and tabs. A rank with no saved entry sees everything
/// released to its agency (opt-in restriction). Individual account releases widen beyond the rank default.
/// </summary>
public interface IPartnerVisibilityPolicyService
{
    /// <summary>Full config (cached). For the admin editor.</summary>
    Task<PartnerVisibilityConfig> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>The saved entry for a rank, or null when unconfigured (sees all).</summary>
    Task<PartnerRankVisibility?> GetRankAsync(PartnerAgency agency, PartnerRank rank, CancellationToken cancellationToken = default);

    /// <summary>Saves (or, when null, clears) a rank's allowlist. Admin only.</summary>
    Task SaveRankAsync(PartnerAgency agency, PartnerRank rank, PartnerRankVisibility? visibility, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Type keys this viewer may access (released by share or rule, narrowed by rank); null = internal user.</summary>
    Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Visible tab slugs for this viewer on a record, minus blocked content; null = all tabs.</summary>
    Task<IReadOnlySet<string>?> GetVisibleTabsAsync(ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default);
}
