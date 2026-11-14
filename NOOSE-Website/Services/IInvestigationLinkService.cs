using System.Security.Claims;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

/// <summary>Links between an investigation file and other records, kept apart from the link engine and filtered per viewer on both ends.</summary>
public interface IInvestigationLinkService
{
    /// <summary>Files linked to a record, only those the viewer may open; empty when the record is hidden from them.</summary>
    Task<List<InvestigationLinkView>> GetForRecordAsync(string recordType, string recordId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);

    /// <summary>Records linked to a file (both directions for file to file), only those the viewer may open.</summary>
    Task<List<InvestigationLinkedRecord>> GetForInvestigationAsync(string investigationId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);

    /// <summary>Files for a link picker: visible ones, or only those whose cover sheet the viewer may edit;
    /// <paramref name="exclude"/> (the file itself, files already linked) is never offered.</summary>
    Task<List<InvestigationPick>> FindInvestigationsAsync(string? text, ClaimsPrincipal viewer, bool editableOnly,
        IReadOnlyCollection<string>? exclude = null, CancellationToken cancellationToken = default);

    /// <summary>Links a record to a file; needs edit on the cover sheet and sight of the record.</summary>
    Task<string> CreateAsync(string investigationId, string recordType, string recordId, string? label, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Changes the label; only from the owning file with edit on its cover sheet.</summary>
    Task UpdateLabelAsync(string linkId, string? label, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Removes a link (soft) from either end: edit on the file's cover sheet, or write on the record plus sight of the file.</summary>
    Task RemoveAsync(string linkId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
