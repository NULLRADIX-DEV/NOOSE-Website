namespace NOOSE_Website.Models.Common;

/// <summary>Input for a followup.</summary>
public sealed record FollowupInput(DateTime DueAt, string? Note, string? ResponsibleAgentId);

/// <summary>A followup prepared for the record panel.</summary>
public sealed record FollowupItem(
    string Id,
    DateTime DueAt,
    string? Note,
    string? ResponsibleAgentId,
    string? ResponsibleCodename,
    bool Done,
    DateTime? DoneAt,
    bool Overdue,
    bool MayEdit);

/// <summary>A due followup resolved for the dashboard list.</summary>
public sealed record FollowupDashboardItem(
    string Id,
    string Display,
    string? Href,
    DateTime DueAt,
    string? Note);

/// <summary>Where a follow-up stands on the personal plan.</summary>
public enum FollowupBucket
{
    Overdue,
    Today,
    ThisWeek,
    Later,
}

/// <summary>An open follow-up of the caller on a record they may see.</summary>
public sealed record FollowupPlanItem(
    string Id,
    string Display,
    string? Href,
    string RecordLabel,
    DateTime DueAt,
    string? Note,
    FollowupBucket Bucket);

/// <summary>The caller's plan; follow-ups on records they may not see are only counted.</summary>
public sealed record FollowupPlan(IReadOnlyList<FollowupPlanItem> Items, int Hidden, bool Capped);

/// <summary>One open follow-up in the leadership backlog; record fields stay null when the viewer may not see it.</summary>
public sealed record FollowupBacklogItem(
    string Id,
    bool RecordVisible,
    string? Display,
    string? Href,
    string? RecordLabel,
    string? Note,
    DateTime DueAt,
    int DaysOverdue,
    string? ResponsibleAgentId,
    string? ResponsibleCodename,
    bool ResponsibleSelectable,
    DateTime? EscalatedAt);

/// <summary>The leadership backlog, oldest due first.</summary>
public sealed record FollowupBacklog(IReadOnlyList<FollowupBacklogItem> Items, bool Capped);
