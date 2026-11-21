using System.Security.Claims;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <summary>Anonymous feature usage: how often each feature was used and on how many agent-days.</summary>
/// <remarks>Admins only, like the section on /statistik.</remarks>
public interface IUsageStatisticsService
{
    Task<UsageStatistics> GetAsync(ClaimsPrincipal actor, StatisticsScope scope, CancellationToken cancellationToken = default);
}
