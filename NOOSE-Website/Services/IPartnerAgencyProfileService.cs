using System.Security.Claims;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Agency-wide partner access: functions, blocked content and release rules. Visibility itself is enforced in <see cref="PartnerVisibility"/>.</summary>
public interface IPartnerAgencyProfileService
{
    /// <summary>The profile of one agency (cached); defaults when none is saved.</summary>
    Task<PartnerAgencyProfileView> GetAsync(PartnerAgency agency, CancellationToken cancellationToken = default);

    /// <summary>The viewer's agency functions; None for internal accounts.</summary>
    Task<PartnerFeature> GetFeaturesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);

    /// <summary>True when the viewer is a partner whose agency has the function switched on.</summary>
    Task<bool> HasFeatureAsync(ClaimsPrincipal user, PartnerFeature feature, CancellationToken cancellationToken = default);

    /// <summary>Replaces an agency's profile and rules in one save. Admin only.</summary>
    Task SaveAsync(PartnerAgency agency, PartnerFeature features, PartnerContent blocked, IReadOnlyList<PartnerRuleView> rules,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
