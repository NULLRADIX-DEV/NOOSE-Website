using System.Security.Claims;
using NOOSE_Website.Data.Entities.Warrants;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Warrants;

namespace NOOSE_Website.Services;

/// <summary>Warrants: an agent requests, leadership signs or refuses, a signed one runs for a set number of days.</summary>
public interface IWarrantService
{
    /// <summary>Warrants visible to the viewer, newest first, optionally narrowed to one state.</summary>
    Task<List<WarrantView>> GetListAsync(WarrantState? state, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Visible warrants concerning one person, newest first.</summary>
    Task<List<WarrantView>> GetForPersonAsync(string personId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Visible warrants filed under one case, newest first.</summary>
    Task<List<WarrantView>> GetForCaseAsync(string caseId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Visible requests awaiting a decision, oldest first, for the inbox.</summary>
    Task<List<WarrantView>> GetPendingAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Requests the viewer could decide right now; zero for anyone who may not sign.</summary>
    Task<int> GetOpenCountAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>One visible warrant with its cited paragraphs; null when missing or not visible.</summary>
    Task<WarrantView?> GetAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>A person or case a new warrant may concern, if the viewer may see it.</summary>
    Task<WarrantTarget?> GetTargetAsync(string type, string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    Task<Warrant> CreateAsync(WarrantInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Signs a request; it is valid from now for <paramref name="validityDays"/> days.</summary>
    Task SignAsync(string id, int validityDays, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task RejectAsync(string id, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Lifts a still valid warrant before it runs out.</summary>
    Task CancelAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Leadership deletes any warrant; the requester only their own open request.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<WarrantView>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
