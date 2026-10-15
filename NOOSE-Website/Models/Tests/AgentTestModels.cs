using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Models.Tests;

/// <summary>Whom a test goes to: single agents, a group, or both; resolved to agents at assignment time.</summary>
public sealed class AgentTestTargets
{
    public List<string> AgentIds { get; set; } = [];

    /// <summary>Group to add; null = single agents only.</summary>
    public AnnouncementAudience? Audience { get; set; }

    /// <summary>Taskforce id for <see cref="AnnouncementAudience.Taskforce"/>.</summary>
    public string? TargetId { get; set; }

    /// <summary>Lowest rank for <see cref="AnnouncementAudience.FromRank"/>.</summary>
    public Rank? MinRank { get; set; }
}

/// <summary>Outcome of one assignment run.</summary>
/// <param name="Skipped">Agents who already hold an ungraded assignment of the same test.</param>
public sealed record AgentTestAssignResult(int Created, int Skipped);

/// <summary>Where an assignment stands.</summary>
public enum AgentTestState
{
    Offen = 0,
    Laeuft = 1,
    ZuKorrigieren = 2,
    Korrigiert = 3,
}

/// <summary>List filter of the management overview.</summary>
public enum AgentTestFilter
{
    Offen = 0,
    ZuKorrigieren = 1,
    Ueberfaellig = 2,
    Erledigt = 3,
    Alle = 4,
}

/// <summary>One assignment in a list; results only once graded, never an answer key.</summary>
public sealed record AgentTestRow(
    string AssignmentId,
    string AgentId,
    string AgentCodename,
    string TestId,
    string TestTitle,
    DateTime AssignedAt,
    string? AssignedByName,
    DateTime? DueAt,
    DateTime? StartedAt,
    DateTime? CompletedAt,
    bool TimedOut,
    int? TimeLimitMinutes,
    DateTime? GradedAt,
    int? Points,
    int? MaxPoints,
    bool? Passed)
{
    public AgentTestState State => GradedAt is not null ? AgentTestState.Korrigiert
        : CompletedAt is not null ? AgentTestState.ZuKorrigieren
        : StartedAt is not null ? AgentTestState.Laeuft
        : AgentTestState.Offen;

    public bool IsOverdue(DateTime nowUtc) => CompletedAt is null && DueAt is { } due && due <= nowUtc;

    public int? Percent => Points is int p && MaxPoints is > 0 ? (int)Math.Round(100.0 * p / MaxPoints.Value) : null;
}

/// <summary>Summary of an own assignment without questions; reading it never starts the clock.</summary>
public sealed record AgentTestStatusView(
    string AssignmentId,
    string Title,
    string? Description,
    bool Completed,
    DateTime? StartedAt,
    DateTime? DeadlineAt,
    DateTime? DueAt,
    bool TimedOut,
    int? TimeLimitMinutes);

/// <summary>The questionnaire of an own running attempt; carries no key and no verdict.</summary>
public sealed record AgentTestView(
    string AssignmentId,
    int AttemptNumber,
    string Title,
    string? Description,
    bool Completed,
    DateTime? DeadlineAt,
    bool TimedOut,
    int? TimeLimitMinutes,
    IReadOnlyList<TestQuestionView> Questions);

/// <summary>The agent's own result: points, percent and verdict, never which question was wrong.</summary>
public sealed record AgentTestResult(
    string Title,
    DateTime? CompletedAt,
    DateTime GradedAt,
    int Points,
    int MaxPoints,
    int? PassPercent,
    bool? Passed)
{
    public int Percent => MaxPoints > 0 ? (int)Math.Round(100.0 * Points / MaxPoints) : 0;
}
