using System.Security.Claims;
using NOOSE_Website.Models.OrgChart;

namespace NOOSE_Website.Services;

/// <summary>Builds the org-chart: the Board and the divisions with their rank tiers, the Administration Division cross-section, and viewer-visible approved taskforces. Read-only.</summary>
public interface IOrgChartService
{
    Task<OrgChartData> GetAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);
}
