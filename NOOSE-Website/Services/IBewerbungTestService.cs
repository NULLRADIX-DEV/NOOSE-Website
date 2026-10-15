using System.Security.Claims;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <summary>Recruiting tests: HRB builds reusable tests, assigns one to an application, the applicant fills it, HRB reviews.</summary>
public interface IBewerbungTestService
{
    // ---- builder (HRB/leadership) ----
    Task<List<BewerbungTest>> GetTestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<BewerbungTest> CreateTestAsync(string title, string? description, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateTestAsync(string id, string title, string? description, bool isActive, int? passPercent, int? timeLimitMinutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Who the test may go to, and the training module an agent passing it gets ticked.</summary>
    Task UpdateUsageAsync(string id, TestUsage usage, string? trainingModuleId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteTestAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestEditModel?> GetEditModelAsync(string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<BewerbungTestQuestion> AddQuestionAsync(string testId, TestQuestionType type, string prompt, bool required, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateQuestionAsync(string questionId, string prompt, bool required, int points, bool? correctYesNo, string? keywords, int? minKeywordHits, bool keepOptionOrder, bool allowMultiple, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteQuestionAsync(string questionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<BewerbungTestOption> AddOptionAsync(string questionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task UpdateOptionAsync(string optionId, string label, bool isCorrect, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task DeleteOptionAsync(string optionId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- assignment + evaluation (HRB/leadership) ----
    Task<BewerbungTestAssignment?> GetAssignmentAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task AssignAsync(string bewerbungId, string testId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestEvaluation?> GetEvaluationAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task SetAwardedPointsAsync(string assignmentId, string questionId, int? points, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task CompleteGradingAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ReopenGradingAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- attempt control (HRB/leadership, write guard first) ----
    Task ExtendAttemptAsync(string bewerbungId, int minutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ResetAttemptAsync(string bewerbungId, string? testId, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- applicant ----
    /// <summary>Summary without questions; never stamps the clock, so the status page may call it.</summary>
    Task<TestStatusView?> GetTestStatusForApplicantAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default);

    /// <summary>The questionnaire, and the only path that starts the attempt clock.</summary>
    /// <remarks>Starting is not a separate call on purpose: a patched client could simply never make one
    /// and would then hold the questions with no clock running.</remarks>
    Task<TestView?> GetAssignedForApplicantAsync(ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
    Task<TestDraftResult> SaveDraftAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
    Task<TestSubmitOutcome> SubmitAnswersAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal applicant, CancellationToken cancellationToken = default);
}
