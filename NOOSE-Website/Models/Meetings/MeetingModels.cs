using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Meetings;

/// <summary>Form model for creating/editing a meeting.</summary>
public class MeetingInput
{
    public string Title { get; set; } = string.Empty;

    /// <summary>Start in local time; the service converts to UTC on save.</summary>
    public DateTime? Start { get; set; }

    public DateTime? End { get; set; }

    public string? Location { get; set; }

    public MeetingStatus Status { get; set; } = MeetingStatus.Planned;
}

/// <summary>Form model for one agenda item; notes and the done flag are saved separately.</summary>
public class MeetingAgendaItemInput
{
    public string Title { get; set; } = string.Empty;
}

/// <summary>One meeting as shown in the list.</summary>
public record MeetingListItem(
    string Id,
    string CaseNumber,
    string Title,
    DateTime Start,
    DateTime? End,
    string? Location,
    MeetingStatus Status,
    int AgendaCount,
    DateTime? AttendanceClosedAt,
    bool OwnSignedOff);

/// <summary>One agent's attendance state; Reason is already nulled for viewers who may not read it.</summary>
public record MeetingAttendanceRow(
    string AgentId,
    string Codename,
    Rank? Rank,
    MeetingAttendanceStatus Status,
    MeetingAbsenceOrigin Origin,
    string? Reason,
    DateOnly? From,
    DateOnly? To,
    DateTime? MarkedAt);

/// <summary>Form model of a measure taken under one agenda item.</summary>
public class MeetingMeasureInput
{
    public MeetingMeasureKind Kind { get; set; } = MeetingMeasureKind.Aufgabe;

    /// <summary>Title of the job; a follow-up has no title of its own and carries it in its note.</summary>
    public string Title { get; set; } = string.Empty;

    public string? Note { get; set; }

    /// <summary>Empty means the person creating it, the same rule as a follow-up.</summary>
    public string? ResponsibleAgentId { get; set; }

    /// <summary>Local date; required for a follow-up, optional for a job.</summary>
    public DateTime? DueAt { get; set; }
}

/// <summary>A measure as the agenda shows it, read live from its job or follow-up.</summary>
public record MeetingMeasureRow(
    string Id,
    MeetingMeasureKind Kind,
    string Title,
    bool Done,
    string Status,
    DateTime? DueAt,
    string? ResponsibleCodename,
    string Href);
