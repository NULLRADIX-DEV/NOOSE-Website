using System.Security.Claims;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Personnel;

namespace NOOSE_Website.Services;

/// <summary>Quick entry of a firearm use, the leadership overview over all reports, and the personnel-file view of one agent.</summary>
public interface IFirearmUseService
{
    /// <summary>Report a firearm use; the actor and the time are stamped here, only the reason comes from the caller.</summary>
    /// <remarks>The division's leadership and the Board hear about it on the bell, without the reason.</remarks>
    Task<FirearmUse> ReportAsync(string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Reports of all agents, newest first; leadership and read-only supervision.</summary>
    Task<List<FirearmUseRow>> GetListAsync(FirearmUseFilter filter, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);

    /// <summary>Reports of one agent, newest first; the agent's own file or the leadership read.</summary>
    Task<List<FirearmUseRow>> GetForAgentAsync(string agentId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);

    /// <summary>Agents that have at least one report, for the overview's agent filter.</summary>
    Task<List<(string Id, string Codename)>> GetAgentOptionsAsync(ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);

    /// <summary>Delete one report; leadership only, and never one's own.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task<List<FirearmUse>> GetTrashAsync(CancellationToken cancellationToken = default);

    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
