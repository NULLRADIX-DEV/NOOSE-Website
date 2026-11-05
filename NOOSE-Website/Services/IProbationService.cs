using System.Security.Claims;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>Onboarding plan of a new agent: mandatory modules, mentor, probation end, reminders and the closing note.</summary>
public interface IProbationService
{
    /// <summary>The agent's latest plan, or null if none was ever opened.</summary>
    Task<ProbationView?> GetAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Open a plan by hand, for agents released before plans existed.</summary>
    Task StartAsync(string agentId, string? mentorId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Set or clear the mentor of the open plan; a new mentor is notified.</summary>
    Task SetMentorAsync(string agentId, string? mentorId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Move the probation end of the open plan; reminders rearm.</summary>
    Task ExtendAsync(string agentId, DateTime endsAtUtc, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Close the open plan and file the closing note in the personnel file.</summary>
    Task CloseAsync(string agentId, ProbationOutcome outcome, string? note, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Configured probation length in days.</summary>
    Task<int> GetDaysAsync(CancellationToken cancellationToken = default);

    /// <summary>Set the probation length for plans opened from now on.</summary>
    Task SetDaysAsync(int days, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Send due reminders; returns how many plans advanced.</summary>
    Task<int> RemindAsync(DateTime nowUtc, CancellationToken cancellationToken = default);
}
