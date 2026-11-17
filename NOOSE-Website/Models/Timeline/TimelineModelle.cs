using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Timeline;

/// <summary>Timeline event category; drives filter chip, icon and colour.</summary>
public enum TimelineCategory
{
    Asset,
    Change,
    Deletion,
    Restoration,
    Classification,
    Doc,
    Observation,
    Photo,
    Relation,
    Membership,
    Allocation,
    Link,
    Comment,
    Source,
    Followup,
    Activity,
    Agenda,
    Attendance,
    SignOff,
    ThreatScore,
}

/// <summary>One event in the unified record timeline; Timestamp is UTC (sort key), Changes only for audit events.</summary>
/// <param name="ActorId">Acting agent, null when unknown or deliberately hidden.</param>
/// <param name="RecordedAt">When the event was entered, if that differs from when it happened.</param>
/// <param name="ImageUrl">Serving route of the photo the event is about, while it still exists.</param>
public sealed record TimelineEntry(
    DateTime Timestamp,
    TimelineCategory Category,
    string Title,
    string? Detail,
    string? ActorName,
    string? Href,
    IReadOnlyList<AuditDisplay.FieldChange>? Changes = null,
    string? ActorId = null,
    DateTime? RecordedAt = null,
    string? ImageUrl = null);
