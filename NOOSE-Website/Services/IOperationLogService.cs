using System.Security.Claims;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Operation log kept while an operation runs; internal only, visibility inherits from the operation.</summary>
public interface IOperationLogService
{
    /// <summary>All lines, oldest first; empty for partners and for an operation the viewer cannot see.</summary>
    Task<List<OperationLogEntry>> GetEntriesAsync(string operationId, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Whether the caller may write now, and whether a line would count as an addendum.</summary>
    Task<OperationLogAccess> GetAccessAsync(string operationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Adds a line; running operations take reports, ended ones take addenda, planned ones nothing.</summary>
    Task<OperationLogEntry> AddAsync(string operationId, OperationLogKind kind, string text, string? location,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Soft-deletes a line; allowed for the author or leadership.</summary>
    Task DeleteAsync(string entryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Writes the log into the operation's Ablauf field, replacing or appending to what is there.</summary>
    Task ApplyToExpiryAsync(string operationId, bool append, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <summary>Write state of one operation log for one caller.</summary>
public sealed record OperationLogAccess(bool MayWrite, bool IsAddendum, OperationStatus Status)
{
    public static readonly OperationLogAccess None = new(false, false, OperationStatus.Planned);
}
