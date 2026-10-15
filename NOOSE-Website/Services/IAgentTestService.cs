using System.Security.Claims;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Models.Tests;

namespace NOOSE_Website.Services;

/// <summary>Internal tests: HRB and leadership assign catalog tests to agents, the agent takes them, HRB grades.</summary>
/// <remarks>A separate interface from the recruiting one on purpose: every member carries exactly one guard, and
/// the taker side must never reach the evaluation.</remarks>
public interface IAgentTestService
{
    // ---- management (HRB/leadership) ----
    /// <summary>Active catalog tests that may go to agents.</summary>
    Task<List<BewerbungTest>> GetAssignableTestsAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<AgentTestAssignResult> AssignAsync(string testId, AgentTestTargets targets, DateTime? dueAt, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<AgentTestRow>> GetOverviewAsync(AgentTestFilter filter, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Every assignment of one agent, newest first; for the personnel file.</summary>
    Task<List<AgentTestRow>> GetForAgentAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<AgentTestAssignment?> GetAssignmentAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestEvaluation?> GetEvaluationAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task SetAwardedPointsAsync(string assignmentId, string questionId, int? points, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task CompleteGradingAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ReopenGradingAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ExtendAsync(string assignmentId, int minutes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ResetAsync(string assignmentId, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task WithdrawAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    // ---- taker (the assigned agent) ----
    /// <summary>Own assignments, open first; graded ones carry points and verdict.</summary>
    Task<List<AgentTestRow>> GetMineAsync(ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Summary without questions; never stamps the clock.</summary>
    Task<AgentTestStatusView?> GetMyStatusAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>The questionnaire, and the only path that starts the attempt clock.</summary>
    Task<AgentTestView?> OpenAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestDraftResult> SaveDraftAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<TestSubmitOutcome> SubmitAsync(string assignmentId, int attemptNumber, IReadOnlyList<TestAnswerInput> answers, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Own result once graded: points, percent, verdict; null before.</summary>
    Task<AgentTestResult?> GetMyResultAsync(string assignmentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <summary>Hands in internal-test attempts whose clock ran out while the browser was closed.</summary>
public interface IAgentTestExpiryService
{
    /// <summary>Closes due attempts; returns how many this pass handed in.</summary>
    Task<int> ExpireDueAsync(CancellationToken cancellationToken = default);

    /// <summary>Sends the due-soon and overdue reminders, each once per assignment; returns how many went out.</summary>
    Task<int> RemindDueAsync(CancellationToken cancellationToken = default);
}
