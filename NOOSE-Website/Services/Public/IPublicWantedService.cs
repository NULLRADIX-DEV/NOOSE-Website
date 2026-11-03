using System.Security.Claims;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>Public wanted notices: the publication snapshot of a person file and everything that decides whether it is outside.</summary>
public interface IPublicWantedService
{
    // ---- outward reads (anonymous) ----

    /// <summary>The whole published board, cached; empty while the module is off.</summary>
    Task<PublicWantedBoard> GetBoardAsync(CancellationToken cancellationToken = default);

    /// <summary>One published notice by its case number; null for every other state.</summary>
    Task<PublicWantedDetail?> GetByCaseNumberAsync(string? caseNumber, CancellationToken cancellationToken = default);

    /// <summary>The advertised bounty of one published notice; null when there is none or the module is off.</summary>
    Task<PublicBounty?> GetBountyAsync(string? caseNumber, CancellationToken cancellationToken = default);

    /// <summary>QR code and short address for the poster of a published notice, on the configured site; null for every other state.</summary>
    Task<WantedPosterLink?> GetPosterLinkAsync(string? caseNumber, CancellationToken cancellationToken = default);

    /// <summary>Profile path a typed short code leads to; null for every miss, so the short route cannot become an existence oracle.</summary>
    Task<string?> ResolveShortLinkAsync(string? code, CancellationToken cancellationToken = default);

    /// <summary>The recently captured notices, cached; empty while the archive module is off.</summary>
    Task<IReadOnlyList<PublicWantedArchiveCard>> GetArchiveAsync(CancellationToken cancellationToken = default);

    /// <summary>How many captures there are in total; 0 while the archive module is off.</summary>
    /// <remarks>
    /// Not <c>GetArchiveAsync().Count</c>: that list is capped at the newest hundred for page weight, and a counter
    /// that stops at the display limit reads as completeness. Same two module switches, same suppression belt.
    /// </remarks>
    Task<int> GetCapturedTotalAsync(CancellationToken cancellationToken = default);

    /// <summary>The photo copy of a published or captured notice; null for every miss, so the endpoint cannot become an existence oracle.</summary>
    Task<PublicWantedPhoto?> GetPublishedPhotoAsync(string? caseNumber, CancellationToken cancellationToken = default);

    /// <summary>Counts one anonymous view of a published notice. A technical counter, deliberately past the audit interceptor.</summary>
    Task CountViewAsync(string? caseNumber, CancellationToken cancellationToken = default);

    // ---- internal reads ----

    /// <summary>Newest live notice of a person file, for the warning banner; null when there is none.</summary>
    Task<PublicWantedBanner?> GetBannerForPersonAsync(string personId, CancellationToken cancellationToken = default);

    /// <summary>The management list.</summary>
    Task<IReadOnlyList<PublicWantedEdit>> GetAllAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The one notice being edited, accusation HTML included.</summary>
    Task<PublicWantedDraft?> GetDraftAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Photo and area choices the editor may offer for a notice; the component never reads the file itself.</summary>
    Task<PublicWantedOptions> GetOptionsAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Id of the person file a photo may be uploaded to for this notice; refuses item and captured notices.</summary>
    Task<string> RequirePhotoUploadAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Newest person notice of a file for the file page, whatever its state; null when there is none.</summary>
    /// <remarks>Item notices are excluded: they hang off the same file but say nothing about the person.</remarks>
    Task<PublicWantedEdit?> GetForPersonAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The vehicle and weapon notices of a file, newest first.</summary>
    Task<IReadOnlyList<PublicWantedEdit>> GetItemsForPersonAsync(string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>The file's vehicles and weapons as possible sources, each flagged if it is already advertised.</summary>
    Task<IReadOnlyList<PublicWantedItemSource>> GetItemSourcesAsync(string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    // ---- writes ----

    /// <summary>Create a draft from a person file and return its id; pulls name and accusation only.</summary>
    Task<string> CreateDraftFromPersonAsync(string personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Create a vehicle draft from one of a file's vehicles and return its id.</summary>
    /// <remarks>
    /// The source row is read once for the prefill and never referenced again: the file's profile children are
    /// replaced wholesale on every save, so a stored source id would be a dangling pointer after the first edit.
    /// The notice keeps the file's id all the same — that is what the suppression belt and the timeline hang on.
    /// </remarks>
    Task<string> CreateDraftFromVehicleAsync(string vehicleId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Create a weapon draft from one of a file's weapons and return its id.</summary>
    Task<string> CreateDraftFromWeaponAsync(string weaponId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Update the snapshot fields of a notice.</summary>
    Task UpdateSnapshotAsync(PublicWantedInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Publish directly (rank ≥ 3) or file a request (rank 1-2); refuses a classified or deleted file either way.</summary>
    Task<PublicWantedPublishOutcome> PublishAsync(string id, string? justification, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Take a notice offline with a reason; works while the module is off.</summary>
    Task RetractAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Mark a published notice as captured, optionally with the reward figure the public total shows.</summary>
    /// <remarks>
    /// <paramref name="publicPaidOut"/> is roleplay backdrop, not money: it books nothing, issues no receipt and
    /// stays out of every internal reward view. It reaches <c>PublicStatistics.RewardsPaid</c> and nothing else.
    /// </remarks>
    Task CapturedAsync(string id, ClaimsPrincipal actor, decimal? publicPaidOut = null,
        CancellationToken cancellationToken = default);

    /// <summary>Correct that publicly shown figure on a captured notice; null takes it out of the total again.</summary>
    /// <remarks>Captured notices only — money shown for an arrest that never happened is a false public claim.</remarks>
    Task SetPublicPaidOutAsync(string id, decimal? publicPaidOut, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Recompute the published hazard level from the file's current score, dropping a manual choice.</summary>
    Task RefreshHazardLevelAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Soft-delete a notice; refused while it is published.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Take every publicly visible notice of a record offline because the record itself changed; no rights guard, the caller passed one.</summary>
    Task RetractForRecordAsync(string personId, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- warning chips ----

    /// <summary>The warning ids assigned to a notice; empty when the actor may not read its file.</summary>
    Task<IReadOnlyList<string>> GetHintIdsAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Replace the warnings of a notice; the diff is logged against the notice.</summary>
    Task SetHintsAsync(string id, IEnumerable<string> hintIds, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- bounty ----

    /// <summary>Say whether the advertised bounty is an exact figure or a ceiling ("bis X").</summary>
    Task SetBountyIsCapAsync(string id, bool isCap, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Drop the public snapshot after a write to a table that feeds it but is owned by another service.</summary>
    /// <remarks>
    /// The bounty shares are such a table. Routing their invalidation through here keeps the cache key and the single
    /// drop site in one file — <c>PublicWantedCacheDisciplineTests</c> holds both, and a second guard holds that every
    /// writer of the share table names this method.
    /// </remarks>
    Task InvalidatePublicViewAsync(CancellationToken cancellationToken = default);

    /// <summary>Flip every notice past its expiry date to Abgelaufen and tell leadership once. Returns how many.</summary>
    /// <remarks>
    /// The whole sweep lives here rather than in the worker: the belt, the status rules and the cache invalidation
    /// must not exist in a second place.
    /// </remarks>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken = default);

    // ---- trash ----

    Task<List<OeffentlicheFahndung>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- publication requests ----

    Task<IReadOnlyList<PublicWantedRequestRow>> GetPendingRequestsAsync(CancellationToken cancellationToken = default);

    Task<int> GetPendingRequestCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Approve a pending request and publish through the same body a direct publication uses.</summary>
    Task ApprovePublicationRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Reject a pending request; the notice falls back to a draft.</summary>
    Task RejectPublicationRequestAsync(string requestId, string? note, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);
}
