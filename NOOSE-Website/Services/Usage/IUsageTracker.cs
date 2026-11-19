using System.Security.Claims;

namespace NOOSE_Website.Services.Usage;

/// <summary>Counts that an agent used a feature, anonymously; see <see cref="UsageCatalog"/> for what can be counted.</summary>
public interface IUsageTracker
{
    /// <summary>Counts one use. Never throws and never blocks the caller on a slow store: counting must not cost a page.</summary>
    Task TrackAsync(ClaimsPrincipal? user, string key, CancellationToken cancellationToken = default);
}
