using System.Security.Claims;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>Agents without a trace for weeks: the radar, the nudge, its thresholds and the page-visit stamp.</summary>
public interface IInactivityRadarService
{
    /// <summary>Selectable agents silent past the first threshold, longest silence first; leadership only.</summary>
    Task<InactivityRadar> GetAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>One agent's row as of now, or null when they are no longer on the radar; leadership only.</summary>
    Task<InactivityRow?> GetRowAsync(string agentId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Ask a silent agent to check in: bell and Discord ping, remembered on the row.</summary>
    /// <remarks>Refuses an agent who has left a trace since the page was loaded.</remarks>
    Task NudgeAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<InactivityThresholds> GetThresholdsAsync(CancellationToken cancellationToken = default);

    Task SaveThresholdsAsync(InactivityThresholds thresholds, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Record a page visit; silent and at most hourly, for internal agents who may write.</summary>
    Task StampSeenAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default);
}
