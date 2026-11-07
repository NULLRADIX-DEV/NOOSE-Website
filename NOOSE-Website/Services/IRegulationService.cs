using System.Security.Claims;
using NOOSE_Website.Models.Regulation;

namespace NOOSE_Website.Services;

/// <summary>The service regulation: sections with versioned paragraphs, acknowledgments and the quota; every rule is enforced here, the pages only mirror it.</summary>
public interface IRegulationService
{
    // ---- reads ----

    /// <summary>The whole regulation for the reader; empty for anyone who may not read it.</summary>
    Task<RegulationDocument> GetDocumentAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>One paragraph with the shown version, or null when it is missing, hidden or the version is unknown; no version shows the valid one.</summary>
    Task<RegulationParagraphView?> GetParagraphAsync(string id, int? version, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Word comparison of two versions, or null when the paragraph is hidden or a version does not exist.</summary>
    Task<RegulationComparison?> CompareAsync(string id, int fromVersion, int toVersion, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Who has acknowledged and who is missing; null for anyone who may not see the quota or the paragraph.</summary>
    Task<RegulationQuota?> GetQuotaAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>How many paragraphs the caller still owes an acknowledgment, for the menu badge.</summary>
    Task<int> GetOpenCountAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>The paragraphs the caller still owes, oldest first, for My Shift.</summary>
    Task<List<OpenRegulationParagraph>> GetOpenForMeAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Live paragraphs matching a heading, a section title or the current wording, for the link dialog.</summary>
    Task<List<RegulationPick>> SearchAsync(string? text, ClaimsPrincipal actor, int take = 20, CancellationToken cancellationToken = default);

    /// <summary>Trashed sections and paragraphs, newest deletion first; the trash page is leadership only.</summary>
    Task<List<RegulationTrashRow>> GetTrashAsync(CancellationToken cancellationToken = default);

    // ---- sections (Board and admin) ----

    Task<string> CreateSectionAsync(string title, bool isPreamble, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task RenameSectionAsync(string id, string title, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves a section up (-1) or down (+1); the foreword always stays first.</summary>
    Task MoveSectionAsync(string id, int delta, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves an empty section to the trash; a section with paragraphs is refused.</summary>
    Task DeleteSectionAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- paragraphs (leadership, a Section Chief only for the own unit) ----

    /// <summary>Adds a paragraph at the end of a section as version 1; returns its id.</summary>
    Task<string> CreateParagraphAsync(string sectionId, RegulationParagraphInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Publishes a new version; refused on a stale <see cref="RegulationVersionInput.ExpectedVersion"/>.</summary>
    Task PublishVersionAsync(string id, RegulationVersionInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves a paragraph up (-1) or down (+1) within its section.</summary>
    Task MoveParagraphAsync(string id, int delta, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Repeals a paragraph with a reason; nobody owes it afterwards and it stays readable.</summary>
    Task RepealParagraphAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Moves a paragraph to the trash.</summary>
    Task DeleteParagraphAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Takes a section or a paragraph out of the trash.</summary>
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- acknowledgments ----

    /// <summary>Acknowledges the current version of one paragraph, which the caller has just read; an older one is refused.</summary>
    Task AcknowledgeAsync(string id, int version, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Acknowledges exactly the versions the caller had in front of them; one that changed in the meantime stays open.</summary>
    Task<RegulationAcknowledgeResult> AcknowledgeAllAsync(IReadOnlyList<RegulationSeen> seen, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
