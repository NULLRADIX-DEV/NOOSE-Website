using System.Security.Claims;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>The released leadership chart: an editorial snapshot, never a projection of the roster.</summary>
/// <remarks>
/// Deliberately not derived from <c>Agent</c> at read time. <c>PublicVisibility</c> keeps agents out of every
/// outward surface; this module is the one exception, and it is an exception a person has to make per entry by
/// releasing it. Nothing goes live because a rank changed.
/// </remarks>
public interface IPublicLeadershipService
{
    // ---- outward ----

    /// <summary>Released entries in their editorial order; empty while the module is off or the kill switch is on.</summary>
    Task<IReadOnlyList<PublicLeadershipCard>> GetPublicAsync(CancellationToken cancellationToken = default);

    /// <summary>The photo of a released entry, or null for every miss — unknown, unreleased, module off, no file.</summary>
    Task<PublicLeadershipPhoto?> GetPublishedPhotoAsync(string key, CancellationToken cancellationToken = default);

    // ---- editorial ----

    Task<IReadOnlyList<PublicLeadershipEdit>> GetAllAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SaveAsync(PublicLeadershipInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Stores a photo copy under the public path; the agent's own avatar is never served.</summary>
    Task SetPhotoAsync(string id, Stream content, string contentType, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Releasing needs a living module; withdrawing never does.</summary>
    Task PublishAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task RetractAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Rewrites a corrected award in every released copy, or removes it when <paramref name="copy"/> is null. Unguarded: callers gate.</summary>
    Task SyncDecorationAsync(string awardId, Models.Decorations.PublicDecorationCopy? copy,
        CancellationToken cancellationToken = default);
}
