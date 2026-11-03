using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>Citizen tips: submission, the handler's inbox, and the conversation between the two.</summary>
/// <remarks>
/// Two audiences, one table. Everything a citizen sees comes back as a <c>CitizenTip*</c> record that structurally
/// carries no agent; everything a handler sees comes back as a <c>Tip*</c> record whose citizen fields are blank while
/// the anonymity promise holds.
/// </remarks>
public interface ITipService
{
    // ---- citizen ----

    /// <summary>Files a tip and returns its case number.</summary>
    /// <remarks>The attachment stream is read before the transaction opens; the caller owns and disposes it.</remarks>
    /// <param name="attachmentSize">Length in bytes; 0 means "not supplied" and skips the size check.</param>
    Task<string> SubmitAsync(TipInput input, Stream? attachment, string? contentType, string? originalName,
        ClaimsPrincipal actor, long attachmentSize = 0, CancellationToken cancellationToken = default);

    /// <summary>The caller's own tips, newest first; empty for an account without a civilian profile.</summary>
    Task<IReadOnlyList<CitizenTipRow>> GetOwnAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>One of the caller's own tips; null when it is not theirs.</summary>
    Task<CitizenTipDetail?> GetOwnDetailAsync(string caseNumber, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Citizen answer in the shared thread.</summary>
    Task ReplyAsCitizenAsync(string caseNumber, string text, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Moves the citizen's read mark; only the owner may.</summary>
    Task MarkCitizenReadAsync(string caseNumber, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Unread agency messages across the caller's own tips.</summary>
    Task<int> GetOwnUnreadCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- handler ----

    Task<IReadOnlyList<TipRow>> GetInboxAsync(TipInboxScope scope, string? search, bool onlyMine,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Tips for the link picker, newest first, across every status.</summary>
    /// <remarks>
    /// Separate from <see cref="GetInboxAsync"/> because that one is bound to a <see cref="TipInboxScope"/> tab, and
    /// a record being linked to a tip does not care which tab the tip currently sits in.
    /// </remarks>
    Task<IReadOnlyList<TipPickRow>> SearchForLinkAsync(string? term, ClaimsPrincipal actor, int take = 20,
        CancellationToken cancellationToken = default);

    Task<TipInboxCounts> GetCountsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Open tips for the navigation badge.</summary>
    Task<int> GetOpenCountAsync(CancellationToken cancellationToken = default);

    /// <summary>Stamps the desk-side read mark the badge counts; the first agent to open a tip clears it.</summary>
    Task MarkAgentReadAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Pins the inbox order by hand, or hands the tip back to the automatic computation with null.</summary>
    Task SetPriorityAsync(string id, int? pinned, string? reason, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    Task<TipDetail?> GetAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The tips filed against one public notice, most urgent first and capped. Carries no citizen field.</summary>
    /// <remarks>
    /// The counterpart of <see cref="GetForLinkedPersonAsync"/>: that one answers "what did this person report",
    /// keyed on the citizen and therefore filtered by the anonymity promise, while this one answers "what came in
    /// about this notice" and is keyed on the notice. Because it names nobody, the promise does not narrow it — an
    /// anonymous tip about a wanted person is exactly what a handler has to be able to read.
    /// </remarks>
    Task<IReadOnlyList<TipNoticeRow>> GetForNoticeAsync(string wantedId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Tips of the citizens tied to this person file; the ones under an anonymity promise are left out.</summary>
    Task<IReadOnlyList<TipHistoryRow>> GetForLinkedPersonAsync(string personId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>The other tips of this one's duplicate group, newest first; empty when it has none.</summary>
    Task<IReadOnlyList<TipDuplicateRow>> GetDuplicatesAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>One thread of one tip; the citizen audience is readable by the owner as well.</summary>
    Task<IReadOnlyList<TipMessageRow>> GetMessagesAsync(string id, TipMessageAudience audience,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task AssignSelfAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task SetStatusAsync(string id, TipStatus status, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task PostInternalNoteAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Agency message to the citizen; the row carries no agent, so it reads as "FIB" outside.</summary>
    Task AskCitizenAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Rewrites one line of either thread; its author only, and never a line the citizen wrote.</summary>
    Task EditMessageAsync(string messageId, string text, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Lifts the anonymity promise; leadership only, and it writes its own audit row.</summary>
    Task ResolveAnonymityAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Attachment access for the delivery endpoint: the owner or a handler, nobody else.</summary>
    Task<TipAttachmentAccess?> GetAttachmentAsync(string id, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>The caller's OWN attachment, addressed by case number; null for anything else.</summary>
    /// <remarks>
    /// The citizen's projection carries no row id on purpose, so the id-addressed overload above is unreachable
    /// from the citizen side and the attachment was shown as dead text. Ownership sits in the predicate.
    /// </remarks>
    Task<TipAttachmentAccess?> GetOwnAttachmentAsync(string caseNumber, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    // ---- reward ----

    /// <summary>Closes a tip as the one that led to the arrest, inside the caller's context and transaction.</summary>
    /// <remarks>
    /// The caller owns the transaction, because the status change and the money must commit together — same shape as
    /// <c>IKassenService.BookAsync(db, …)</c>. Saves into that context; it never commits. The status rules stay here rather than being copied into the reward
    /// service: <see cref="TipRules.MayBeRewarded"/> decides, and a second payment is kept off by the receipt rather
    /// than by <see cref="TipStatus.FuehrteZurErgreifung"/>.
    /// </remarks>
    Task<TipRewardTarget> MarkRewardedAsync(AppDbContext db, string tipId, decimal amount, string receiptNumber,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Post-commit follow-ups of a rewarded tip: trust counter, inbox order, citizen notice, live update.</summary>
    /// <remarks>Best effort throughout — a notification must never topple a committed payout.</remarks>
    Task AfterRewardAsync(IReadOnlyList<TipRewardTarget> targets, CancellationToken cancellationToken = default);

    // ---- trash ----

    Task<List<Hinweis>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
