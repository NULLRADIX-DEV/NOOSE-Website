using NOOSE_Website.Data.Entities.Administration;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Administration;

/// <summary>One person on the workspace roster.</summary>
public sealed record AdministrationMemberRow(string Id, string Codename, Rank? Rank, Division? Division, bool IsHrb, bool IsBoard);

/// <summary>An application in or past the security check.</summary>
public sealed record SecurityCheckRow(string Id, string CaseNumber, string Name, BewerbungStatus Status, bool? Passed,
    DateTime SubmittedAt, DateTime? DecidedAt, string? AssignedAgentName);

/// <summary>An agent's latest evaluation, with the two warnings the regulation knows.</summary>
public sealed record EvaluationOverviewRow(string AgentId, string Codename, Rank? Rank, DateTime? LastDate,
    EvaluationGrade? LastGrade, bool Stale, bool Negative);

/// <summary>An open promotion request, read-only for the Administration.</summary>
public sealed record PromotionOverviewRow(string Id, string AgentId, string Codename, Rank? CurrentRank, Rank TargetRank,
    string? RequesterName, DateTime CreatedAt, bool BlockedByProceeding);

public sealed record RelationshipRow(string Id, string AgentAId, string AgentAName, string AgentBId, string AgentBName,
    AgentRelationshipKind Kind, DateTime? Since, string? Note);

public sealed record DecorationProposalRow(string Id, string AgentId, string AgentName, string DecorationId,
    string DecorationName, string Reason, DecorationProposalStatus Status, string? ProposerName, DateTime CreatedAt,
    string? DecidedByName, DateTime? DecidedAt);

public sealed record DisciplinaryRow(string Id, string CaseNumber, string AgentId, string AgentName,
    DisciplinaryStatus Status, bool Simplified, bool ProvisionallySuspended, DateTime OpenedAt, DateTime? ClosedAt,
    DisciplinaryMeasure? Measure);

/// <summary>A proceeding as its file shows it; <see cref="Name"/> is the clear name only where the viewer may see it.</summary>
public sealed record DisciplinaryView(DisciplinaryProceeding Row, string Codename, string Name, Rank? CurrentRank,
    bool MayHandle);

public sealed record TransferRow(string Id, string AgentId, string AgentName, Division? From, Division To, string Reason,
    TransferRequestStatus Status, string? RequesterName, DateTime CreatedAt, string? HearingNote, string? HearingByName,
    DateTime? HearingAt, string? DecisionNote, string? DecidedByName, DateTime? DecidedAt, string? RequesterId);

public sealed record TransferHistoryRow(string AgentId, string AgentName, Division? From, Division? To, DateTime At,
    string? ByName);

public sealed record ServiceReferenceRow(string Id, string AgentId, string AgentName, ServiceReferenceReason Reason,
    EvaluationGrade? Grade, DateTime IssuedOn, string? IssuedByName);

/// <summary>Everything the printed reference needs.</summary>
public sealed record ServiceReferencePrint(ServiceReference Row, string Name, IReadOnlyList<AgentRankHistory> Ranks,
    IReadOnlyList<DivisionTransfer> Transfers, IReadOnlyList<(string Name, DateTime On)> Decorations);

public sealed record ParliamentRow(string Id, string CaseNumber, string Title, ParliamentaryInquiryStatus Status,
    DateTime ReceivedAt, DateTime DueAt, string? SubmitterName, string? AssignedToName, DateTime? AnsweredAt, bool Overdue);

/// <summary>An inquiry; <see cref="ParliamentaryInquiry.Answer"/> is cleared where the viewer may not see it yet.</summary>
public sealed record ParliamentView(ParliamentaryInquiry Row, string? AssignedToName, bool MayAnswer);

public sealed record AdministrationChatState(DateTime? Since, int Unread)
{
    public static readonly AdministrationChatState None = new(null, 0);
}

public class DisciplinaryOpenInput
{
    public string AgentId { get; set; } = string.Empty;
    public string Grounds { get; set; } = string.Empty;
    public bool Simplified { get; set; }
    public bool ProvisionallySuspended { get; set; }
}

public class DisciplinaryCloseInput
{
    /// <summary>True ends in a notice with a measure, false discontinues.</summary>
    public bool Decide { get; set; }
    public DisciplinaryMeasure? Measure { get; set; }
    public string Facts { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public string RightsNotice { get; set; } = string.Empty;
}

public class TransferInput
{
    public string AgentId { get; set; } = string.Empty;
    public Division ToDivision { get; set; } = Division.Cid;
    public string Reason { get; set; } = string.Empty;
}

public class ServiceReferenceInput
{
    public string AgentId { get; set; } = string.Empty;
    public ServiceReferenceReason Reason { get; set; } = ServiceReferenceReason.OnRequest;
    public EvaluationGrade? Grade { get; set; }
    public string TextHtml { get; set; } = string.Empty;
    public DateTime? IssuedOn { get; set; }
}

public class RelationshipInput
{
    public string AgentAId { get; set; } = string.Empty;
    public string AgentBId { get; set; } = string.Empty;
    public AgentRelationshipKind Kind { get; set; } = AgentRelationshipKind.Partnership;
    public DateTime? Since { get; set; }
    public string? Note { get; set; }
}

public class DecorationProposalInput
{
    public string AgentId { get; set; } = string.Empty;
    public string DecorationId { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public class ParliamentInput
{
    public string Title { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    public DateTime? DueAt { get; set; }
}
