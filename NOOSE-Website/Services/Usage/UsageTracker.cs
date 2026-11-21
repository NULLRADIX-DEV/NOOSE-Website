using System.Security.Claims;
using NOOSE_Website.Authorization;

namespace NOOSE_Website.Services.Usage;

/// <inheritdoc cref="IUsageTracker" />
public sealed class UsageTracker(UsageCollector collector, INavPreferencesService prefs, ILogger<UsageTracker> logger)
    : IUsageTracker
{
    public async Task TrackAsync(ClaimsPrincipal? user, string key, CancellationToken cancellationToken = default)
    {
        try
        {
            // unknown key: drop
            if (user is null || UsageCatalog.Find(key) is null || !UsageRules.IsCounted(user))
            {
                return;
            }
            var agentId = user.GetAgentId()!;
            if (UsageRules.IsOptedOut(await prefs.GetAsync(agentId, cancellationToken)))
            {
                return;
            }
            collector.Add(agentId, key);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Usage counting failed.");
        }
    }
}
