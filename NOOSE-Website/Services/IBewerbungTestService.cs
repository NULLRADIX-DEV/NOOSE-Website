using System.Security.Claims;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <summary>Recruiting tests: HRB builds reusable tests, assigns them to an application, the applicant fills them, HRB reviews.</summary>
public interface IBewerbungTestService
{
    // ---- builder (HRB/leadership) ----
    Task<List<BewerbungTest>> GetTestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<BewerbungTest> CreateTestAsync(string title, string? description, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateTestAsync(string id, string title, string? description, bool isActive, int? passPercent, int? timeLimitMinutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Who the test may go to, and the training module an agent passing it gets ticked.</summary>
    /// <param name="blockedPages">Nav keys locked while an agent's attempt runs; null leaves them as they are.</param>
    Task UpdateUsageAsync(string id, TestUsage usage, string? trainingModuleId, IReadOnlyCollection<string>? blockedPages, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteTestAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestEditModel?> GetEditModelAsync(string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<BewerbungTestQuestion> AddQuestionAsync(string testId, TestQuestionType type, string prompt, bool required, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateQuestionAsync(string questionId, string prompt, bool required, int points, bool? correctYesNo, string? keywords, int? minKeywordHits, bool keepOptionOrder, bool allowMultiple, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteQuestionAsync(string questionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<BewerbungTestOption> AddOptionAsync(string questionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateOptionAsync(string optionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteOptionAsync(string optionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- assignment + evaluation (HRB/leadership) ----
    /// <summary>All tests of one application, oldest first, each with its test loaded.</summary>
    Task<List<BewerbungTestAssignment>> GetAssignmentsAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Every application's test attempts, keyed by application id, oldest first, for the list overview.</summary>
    Task<Dictionary<string, List<BewerbungTestAssignment>>> GetAssignmentsByApplicationAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task AssignAsync(string bewerbungId, string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestEvaluation?> GetEvaluationAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task SetAwardedPointsAsync(string assignmentId, string questionId, int? points, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task CompleteGradingAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ReopenGradingAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- blind grading (HRB/leadership) ----
    /// <summary>Handed-in, ungraded tests of open applications, oldest first, without the applicant's name.</summary>
    Task<List<BlindSubmission>> GetBlindQueueAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>One attempt by its own id, for the blind grading page.</summary>
    Task<BewerbungTestAssignment?> GetAssignmentByIdAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- attempt control (HRB/leadership, write guard first) ----
    Task ExtendAttemptAsync(string assignmentId, int minutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ResetAttemptAsync(string assignmentId, string? testId, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- applicant ----
    /// <summary>Summaries of every own test without questions; never stamps the clock, so the status page may call it.</summary>
    Task<List<TestStatusView>> GetTestStatusesForApplicantAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default);

    /// <summary>The questionnaire, and the only path that starts the attempt clock.</summary>
    /// <remarks>Starting is not a separate call on purpose: a patched client could simply never make one
    /// and would then hold the questions with no clock running.</remarks>
    Task<TestView?> GetAssignedForApplicantAsync(string assignmentId, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
    Task<TestDraftResult> SaveDraftAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
    Task<TestSubmitOutcome> SubmitAnswersAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
}
