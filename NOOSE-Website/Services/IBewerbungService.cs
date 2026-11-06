using System.Security.Claims;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <summary>Job applications: public submission, the HRB-internal thread and the applicant conversation, status workflow, person link.</summary>
public interface IBewerbungService
{
    /// <summary>The signed-in applicant's own application (most recent, non-deleted), or null.</summary>
    Task<Bewerbung?> GetOwnAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default);

    /// <summary>Submit a new application. Applicant only; one open application at a time, blocked while banned or blacklisted, and while Discord says the applicant is not on the FIB server (an unanswered check lets it through).</summary>
    /// <param name="attachmentSize">Length in bytes; 0 means "not supplied" and skips the size check.</param>
    Task<Bewerbung> SubmitAsync(BewerbungSubmitModel model, Stream? attachment, string? originalName, string? contentType,
        ClaimsPrincipal applicant, long attachmentSize = 0, CancellationToken cancellationToken = default);

    /// <summary>All applications (newest first) for the management overview. HRB/leadership only.</summary>
    Task<List<Bewerbung>> ListAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>One application for the HRB detail view. HRB/leadership only.</summary>
    Task<Bewerbung?> GetForHrbAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The application if the actor may access its attachment (owner or HRB/leadership), else null.</summary>
    Task<Bewerbung?> GetForFileAccessAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Assign the calling agent as the handler. HRB/leadership only.</summary>
    Task AssignSelfAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Move the application to a new status (validated transition). HRB/leadership only.</summary>
    Task SetStatusAsync(string id, BewerbungStatus target, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Put an accepted or rejected application back into an open stage. HRB/leadership with write access only.</summary>
    Task ReopenAsync(string id, BewerbungStatus target, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Record the security-check result; a failed check rejects the application. HRB/leadership only.</summary>
    Task SetSecurityResultAsync(string id, bool passed, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Link or unlink an existing Person file. HRB/leadership only.</summary>
    Task LinkPersonAsync(string id, string? personId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Info about the linked Person (threat score), or null. HRB/leadership only.</summary>
    Task<LinkedPersonInfo?> GetLinkedPersonAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Messages of one audience, chronological. Intern requires HRB/leadership; Bewerber requires owner or HRB/leadership.</summary>
    Task<List<BewerbungMessage>> GetMessagesAsync(string id, BewerbungMessageAudience audience, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Post into the HRB-internal thread. HRB/leadership only.</summary>
    Task<BewerbungMessage> PostInternalAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Edit an own message in the HRB-internal thread. Author only, HRB/leadership only.</summary>
    Task<BewerbungMessage> EditInternalMessageAsync(string messageId, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Post a message to the applicant; the real name is redacted before persisting. HRB/leadership only.</summary>
    Task<BewerbungMessage> PostToApplicantAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Applicant posts into their own conversation.</summary>
    Task<BewerbungMessage> PostAsApplicantAsync(string id, string text, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
}
