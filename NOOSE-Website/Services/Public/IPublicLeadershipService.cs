using System.Security.Claims;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>The public roster on /fuehrung, projected from the agents at read time.</summary>
/// <remarks>
/// Who appears is <c>AgentSelection.OnlySelectable</c> with a confirmed real name; the leadership band is the rank
/// floor of <c>LeadershipChart</c> on top of that. Only the phone numbers are kept by hand.
/// </remarks>
public interface IPublicLeadershipService
{
    /// <summary>The roster as it goes out; empty while the module is off or the kill switch is on.</summary>
    Task<PublicRoster> GetPublicAsync(CancellationToken cancellationToken = default);

    /// <summary>The current leadership band with its phone numbers, for the settings panel.</summary>
    Task<IReadOnlyList<PublicLeadershipEdit>> GetLeadershipAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the phone shown on a leadership card; blank removes it.</summary>
    Task SetPhoneAsync(string agentId, string? phone, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);
}
