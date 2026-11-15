using System.Security.Claims;

namespace NOOSE_Website.Services;

/// <summary>
/// Per partner-unit default visibility of record types and tabs. A unit without an allowlist sees everything
/// released to it (opt-in restriction). Individual account releases widen beyond the unit default.
/// </summary>
public interface IPartnerVisibilityPolicyService
{
    /// <summary>Type keys this viewer may access (released by share or rule, narrowed by the unit allowlist); null = internal user.</summary>
    Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>Visible tab slugs for this viewer on a record, minus blocked content; null = all tabs.</summary>
    Task<IReadOnlySet<string>?> GetVisibleTabsAsync(ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default);
}
