using System.Security.Claims;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <summary>Anonymous feature usage: how often each feature was used and on how many agent-days.</summary>
/// <remarks>Gated like the section on /statistik: leadership and the read-only supervision.</remarks>
public interface IUsageStatisticsService
{
    Task<UsageStatistics> GetAsync(ClaimsPrincipal actor, StatisticsScope scope, CancellationToken cancellationToken = default);
}
