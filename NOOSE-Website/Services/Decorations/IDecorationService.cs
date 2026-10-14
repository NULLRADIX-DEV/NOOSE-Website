using System.Security.Claims;
using NOOSE_Website.Models.Decorations;

namespace NOOSE_Website.Services.Decorations;

/// <summary>Decorations leadership awards, their catalog and the certificates printed from them and from the rank history.</summary>
public interface IDecorationService
{
    /// <summary>The whole catalog with award counts; empty outside the house.</summary>
    Task<IReadOnlyList<DecorationView>> GetCatalogAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Creates or updates a catalog entry; leadership with write access.</summary>
    Task<string> SaveDecorationAsync(DecorationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Removes an entry that was never awarded; one that was must be switched off instead.</summary>
    Task DeleteDecorationAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Signatory and place of every certificate.</summary>
    Task<CertificateSettings> GetCertificateSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Who may be picked as signatory, plus the stored one even if no longer eligible.</summary>
    Task<IReadOnlyList<SignatoryOption>> GetSignatoryOptionsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Stores signatory and place; leadership with write access.</summary>
    Task SaveCertificateSettingsAsync(CertificateSettings input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Awards a decoration and runs the chosen announcements; returns the award id.</summary>
    Task<string> AwardAsync(DecorationAwardInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Corrects decoration, date or citation of a standing award; announces nothing.</summary>
    Task UpdateAsync(string id, DecorationAwardInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Revokes an award with a reason; the row stays, struck through.</summary>
    Task RevokeAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Every award of one agent, revoked ones included, newest first.</summary>
    Task<IReadOnlyList<DecorationAwardView>> GetForAgentAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Plaques of standing awards per agent; agents without any are absent.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<DecorationPlaqueView>>> GetPlaquesAsync(
        IReadOnlyCollection<string> agentIds, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The newest standing awards of the last weeks for the dashboard.</summary>
    Task<IReadOnlyList<RecentDecorationView>> GetRecentAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Certificate of an award; null unless leadership or the recipient asks and it still stands.</summary>
    Task<CertificateView?> GetCertificateAsync(string awardId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Appointment or promotion certificate of a rank-history row; null for anything else.</summary>
    Task<CertificateView?> GetRankCertificateAsync(string historyId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
