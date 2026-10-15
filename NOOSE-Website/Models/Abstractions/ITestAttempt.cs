namespace NOOSE_Website.Models.Abstractions;

/// <summary>The clock and grading state of one test attempt, shared by applicant and agent tests.</summary>
public interface ITestAttempt
{
    string Id { get; }
    string TestId { get; }
    DateTime? StartedAt { get; }
    DateTime? DeadlineAt { get; }
    int? TimeLimitMinutes { get; }
    int ExtraMinutes { get; }
    bool TimedOut { get; }
    int AttemptCount { get; }
    DateTime? CompletedAt { get; }
    DateTime? GradedAt { get; }
    string? GradedByName { get; }
    int? FinalPoints { get; }
    int? FinalMaxPoints { get; }
    int? FinalPassPercent { get; }
}

/// <summary>One answer row of a test attempt, shared by applicant and agent tests.</summary>
public interface ITestAnswer
{
    string Id { get; }
    DateTime CreatedAt { get; }
    string QuestionId { get; }
    string? SelectedOptionId { get; set; }
    string? SelectedOptionIds { get; set; }
    string? FreeTextAnswer { get; set; }
    bool? ManualCorrect { get; }
    int? ManualPoints { get; }
}
