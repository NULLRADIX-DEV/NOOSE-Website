using System.Security.Claims;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Models.Directives;

namespace NOOSE_Website.Services;

/// <summary>Numbered service directives with versions, acknowledgments and the quota; every rule is enforced here, the pages only mirror it.</summary>
public interface IDirectiveService
{
    /// <summary>Visible directives by number; <paramref name="repealed"/> picks the repealed ones instead of the standing ones.</summary>
    Task<List<DirectiveRow>> GetListAsync(ClaimsPrincipal actor, bool repealed, CancellationToken cancellationToken = default);

    /// <summary>One directive with the shown version, or null when it is missing, hidden or the version is unknown; no version shows the valid one.</summary>
    Task<DirectiveView?> GetDetailAsync(string id, int? version, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Issues a directive as version 1 under the next number; returns its id. Leadership only.</summary>
    Task<string> CreateAsync(DirectiveInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version; refused on a stale <see cref="DirectiveVersionInput.ExpectedVersion"/>. Leadership only.</summary>
    Task PublishVersionAsync(string id, DirectiveVersionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges the newest version, which the caller has just read; an older one is refused.</summary>
    Task AcknowledgeAsync(string id, int version, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Repeals a directive with a reason; nobody owes it afterwards and its number stays taken. Leadership only.</summary>
    Task RepealAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves a directive to the trash. Leadership only.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deleted directives, newest deletion first; the trash page is leadership only.</summary>
    Task<List<Directive>> GetTrashAsync(CancellationToken cancellationToken = default);

    /// <summary>Takes a directive out of the trash. Leadership only.</summary>
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>How many directives the caller still owes an acknowledgment, for the menu badge.</summary>
    Task<int> GetOpenCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The directives the caller still owes, oldest first, for My Shift.</summary>
    Task<List<OpenDirective>> GetOpenForMeAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Who has acknowledged and who is missing; null for anyone who may not see the quota or the directive.</summary>
    Task<DirectiveQuota?> GetQuotaAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Word comparison of two versions, or null when the directive is hidden or a version does not exist.</summary>
    Task<DirectiveComparison?> CompareAsync(string id, int fromVersion, int toVersion, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Visible directives matching a number (DA-07) or a title fragment, for the link dialog.</summary>
    Task<List<DirectivePick>> SearchAsync(string? text, ClaimsPrincipal actor, int take = 20, CancellationToken cancellationToken = default);
}
