using System.Security.Claims;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>The public roster on /fuehrung, projected from the agents at read time.</summary>
/// <remarks>
/// Who appears is <c>AgentSelection.OnlySelectable</c> with a confirmed real name; the leadership band is the rank
/// floor of <c>LeadershipChart</c> on top of that. Only phone numbers and photos are kept by hand.
/// </remarks>
public interface IPublicLeadershipService
{
    /// <summary>The roster as it goes out; empty while the module is off or the kill switch is on.</summary>
    Task<PublicRoster> GetPublicAsync(CancellationToken cancellationToken = default);

    /// <summary>The photo behind a card, or null for every miss — unknown, not leadership, module off, no file.</summary>
    Task<PublicLeadershipPhoto?> GetPublishedPhotoAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>The current leadership band with phone and photo, for the settings panel.</summary>
    Task<IReadOnlyList<PublicLeadershipEdit>> GetLeadershipAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Sets the phone shown on a leadership card; blank removes it.</summary>
    Task SetPhoneAsync(string agentId, string? phone, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Stores a photo copy under the public path; the agent's own avatar is never served.</summary>
    Task SetPhotoAsync(string agentId, Stream content, string contentType, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    Task RemovePhotoAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Whether the actor chose to stay off the public roster.</summary>
    Task<bool> IsOwnHiddenAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The actor's own choice to stay off the public roster; nobody decides it for someone else.</summary>
    Task SetOwnHiddenAsync(bool hidden, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
