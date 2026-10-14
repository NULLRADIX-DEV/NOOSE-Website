using System.Security.Claims;

namespace NOOSE_Website.Services;

/// <summary>Keeps the standing Discord threat board in step with the faction scores.</summary>
public interface IFactionThreatBoardService
{
    /// <summary>Worker pass: edit on change, post when missing; <paramref name="force"/> also edits an unchanged board.</summary>
    Task<ThreatBoardOutcome> RunAsync(bool force = false, CancellationToken cancellationToken = default);

    /// <summary>Bring the board up to date now, for the settings page; writing leadership only.</summary>
    Task<ThreatBoardOutcome> RefreshAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <summary>What one board pass did.</summary>
public enum ThreatBoardOutcome
{
    /// <summary>Discord is off or the board has no webhook.</summary>
    Off,
    Unchanged,
    Edited,
    Posted,

    /// <summary>Discord refused; the next pass tries again.</summary>
    Failed,
}
