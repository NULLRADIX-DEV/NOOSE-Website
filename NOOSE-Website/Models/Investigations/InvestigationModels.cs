using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Investigations;

/// <summary>One row of the investigation list.</summary>
public sealed record InvestigationListItem(
    string Id, string CaseNumber, string Title, InvestigationStatus Status, string TaskforceId, string? TaskforceName,
    string? SidCaseNumber, DateTime OpenedAt, IReadOnlyList<string> Prosecutors, int Participants);

/// <summary>Cover data of an investigation (the "Deckblatt").</summary>
public sealed record InvestigationInput(
    string Title, string TaskforceId, string? SidCaseNumber, string? FiveNetCaseNumber, InvestigationStatus Status,
    DateTime OpenedAt, string? Indictment);

/// <summary>The file as one viewer may see it: only readable tabs are filled, hidden entries are left out.</summary>
public sealed class InvestigationDetail
{
    public required string Id { get; init; }
    public required string CaseNumber { get; init; }
    public required string Title { get; init; }
    public required InvestigationStatus Status { get; init; }
    public required string TaskforceId { get; init; }
    public string? TaskforceName { get; init; }
    public string? TaskforceCaseNumber { get; init; }
    public bool TaskforceVisible { get; init; }
    public string? SidCaseNumber { get; init; }
    public string? FiveNetCaseNumber { get; init; }
    public DateTime OpenedAt { get; init; }
    public string? Indictment { get; init; }
    public string? NarrativeHtml { get; init; }
    public DateTime CreatedAt { get; init; }
    public required InvestigationAccessView Access { get; init; }
    public IReadOnlyList<string> Prosecutors { get; init; } = Array.Empty<string>();
    public IReadOnlyList<InvestigationChargeView> Charges { get; init; } = Array.Empty<InvestigationChargeView>();
    public IReadOnlyList<InvestigationPersonView> People { get; init; } = Array.Empty<InvestigationPersonView>();
    public IReadOnlyList<InvestigationEvidenceView> Evidence { get; init; } = Array.Empty<InvestigationEvidenceView>();
    public IReadOnlyList<InvestigationInterrogationView> Interrogations { get; init; } = Array.Empty<InvestigationInterrogationView>();
    public IReadOnlyList<InvestigationTaskView> Tasks { get; init; } = Array.Empty<InvestigationTaskView>();
    public IReadOnlyList<InvestigationNoteView> Notes { get; init; } = Array.Empty<InvestigationNoteView>();
    public IReadOnlyList<InvestigationParticipantView> Participants { get; init; } = Array.Empty<InvestigationParticipantView>();

    /// <summary>Entry id → the accounts and units it is limited to (only filled for managers).</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<InvestigationTarget>> Restrictions { get; init; }
        = new Dictionary<string, IReadOnlyList<InvestigationTarget>>();

    /// <summary>Ids of entries limited to some participants (everyone who sees them sees the marker).</summary>
    public IReadOnlySet<string> RestrictedIds { get; init; } = new HashSet<string>();
}

/// <summary>An account or a unit, as picked for participation or for an entry restriction.</summary>
public sealed record InvestigationTarget(string? AgentId, string? PartnerUnitId, string Label);

public sealed record InvestigationChargeView(string Id, string? LawId, string Paragraph, string LawBook, string Description,
    InvestigationChargeCheck Check, int SortOrder);

public sealed record InvestigationChargeInput(string? Id, string? LawId, string Paragraph, string LawBook, string Description,
    InvestigationChargeCheck Check);

public sealed record InvestigationPersonView(string Id, string Name, string? PersonId, string? PersonCaseNumber, string? Phone,
    string Role, InvestigationPersonStatus Status, string? Note);

public sealed record InvestigationPersonInput(string? Id, string Name, string? PersonId, string? Phone, string Role,
    InvestigationPersonStatus Status, string? Note);

public sealed record InvestigationEvidenceView(string Id, int Number, string Name, string? Description, string? Owner,
    string? Officer, InvestigationEvidenceStatus Status, DateTime? SecuredAt);

public sealed record InvestigationEvidenceInput(string? Id, string Name, string? Description, string? Owner, string? Officer,
    InvestigationEvidenceStatus Status, DateTime? SecuredAt);

public sealed record InvestigationQa(string Question, string Answer);

public sealed record InvestigationInterrogationView(string Id, string? InvestigationPersonId, string Name, string Role,
    string? Summons, DateTime? HeldAt, string? Officers, string? ProsecutorPresent, string? Summary, string? ProtocolReference,
    bool Usable, IReadOnlyList<InvestigationQa> Entries);

public sealed record InvestigationInterrogationInput(string? Id, string? InvestigationPersonId, string Name, string Role,
    string? Summons, DateTime? HeldAt, string? Officers, string? ProsecutorPresent, string? Summary, string? ProtocolReference,
    bool Usable, IReadOnlyList<InvestigationQa> Entries);

public sealed record InvestigationTaskView(string Id, string Text, bool Done, DateTime? DoneAt, string? AssigneeId,
    string? AssigneeName, DateTime? DueAt, int SortOrder);

public sealed record InvestigationTaskInput(string? Id, string Text, string? AssigneeId, DateTime? DueAt);

public sealed record InvestigationNoteView(string Id, string Text, string? AuthorName, string? AuthorId, DateTime CreatedAt);

/// <summary>A participant row with what it grants (defaults resolved for display).</summary>
public sealed record InvestigationParticipantView(
    string Id, string? AgentId, string? PartnerUnitId, string Label, string? Detail, InvestigationRole Role, bool IsUnit,
    IReadOnlyDictionary<string, InvestigationAccessLevel> Overrides,
    IReadOnlyDictionary<string, InvestigationAccessLevel> Defaults);

public sealed record InvestigationParticipantInput(
    string? Id, string? AgentId, string? PartnerUnitId, InvestigationRole Role,
    IReadOnlyDictionary<string, InvestigationAccessLevel> Overrides);

/// <summary>One line of the file's history.</summary>
public sealed record InvestigationHistoryEntry(DateTime At, string? Who, string Tab, string What, string? Changes);
