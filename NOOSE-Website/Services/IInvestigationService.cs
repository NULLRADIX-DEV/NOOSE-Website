using System.Security.Claims;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

/// <summary>Investigation files run with the DoJ. Every read and write resolves the viewer's rights through <see cref="InvestigationAccess"/>.</summary>
public interface IInvestigationService
{
    /// <summary>Investigations the viewer may open, optionally of one taskforce or naming one person.</summary>
    Task<List<InvestigationListItem>> GetListAsync(ClaimsPrincipal viewer, string? taskforceId = null, string? personId = null,
        CancellationToken cancellationToken = default);

    /// <summary>The file as the viewer may see it; null when it does not exist for them.</summary>
    Task<InvestigationDetail?> GetAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>The viewer's rights on one investigation.</summary>
    Task<InvestigationAccessView> GetAccessAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Taskforces the viewer may open an investigation on.</summary>
    Task<List<(string Id, string Label)>> GetCreatableTaskforcesAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Whether the viewer may open an investigation at all (any taskforce).</summary>
    Task<bool> MayCreateAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Opens an investigation; the creator becomes its lead. Returns the id.</summary>
    Task<string> CreateAsync(InvestigationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Opens an investigation with everything an import read; one save, one case number.</summary>
    Task<string> CreateWithContentAsync(InvestigationInput input, string? narrativeHtml,
        IReadOnlyList<InvestigationChargeInput> charges, IReadOnlyList<InvestigationPersonInput> people,
        IReadOnlyList<InvestigationEvidenceInput> evidence, IReadOnlyList<InvestigationInterrogationInput> interrogations,
        IReadOnlyList<(string Text, bool Done)> tasks, IReadOnlyList<string> notes,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Saves the cover sheet (needs edit on the overview tab); moving it to another taskforce needs leadership.</summary>
    Task UpdateAsync(string id, InvestigationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task SetNarrativeAsync(string id, string? html, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SaveChargeAsync(string id, InvestigationChargeInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SavePersonAsync(string id, InvestigationPersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SaveEvidenceAsync(string id, InvestigationEvidenceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SaveInterrogationAsync(string id, InvestigationInterrogationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> SaveTaskAsync(string id, InvestigationTaskInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task SetTaskDoneAsync(string id, string taskId, bool done, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<string> AddNoteAsync(string id, string text, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Deletes one entry of a tab (soft); a note only its author or a manager.</summary>
    Task DeleteEntryAsync(string id, string itemType, string itemId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Accounts and units that can be added: internal agents, and partners of the agencies taking part in the taskforce.</summary>
    Task<List<InvestigationTarget>> GetCandidatesAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    Task<string> SaveParticipantAsync(string id, InvestigationParticipantInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task RemoveParticipantAsync(string id, string participantId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Limits one entry to the given accounts and units; an empty list lifts the limit.</summary>
    Task SetRestrictionAsync(string id, string itemType, string itemId, IReadOnlyList<InvestigationTarget> targets,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<InvestigationHistoryEntry>> GetHistoryAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Deletes the whole file (soft); needs full access.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<Investigation>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
