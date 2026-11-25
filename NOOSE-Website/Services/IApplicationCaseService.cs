using System.Security.Claims;

namespace NOOSE_Website.Services;

/// <summary>Auto-provisions the "Sicherheitsüberprüfung" document for an application.</summary>
public interface IApplicationCaseService
{
    /// <summary>Idempotently create the document (from the configured template) and file it on the application; no-op when disabled or already there.</summary>
    Task EnsureSecurityCheckCaseAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
