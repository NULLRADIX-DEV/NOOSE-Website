using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Account management for leadership/admin: release inbox, rank/role assignment, kill-switch. Block/unblock/rank changes rotate the SecurityStamp and end the agent's sessions.</summary>
public interface IAgentManagementService
{
    Task<List<Agent>> GetPendingAsync(CancellationToken cancellationToken = default);
    Task<List<Agent>> GetAllAsync(CancellationToken cancellationToken = default);

    /// <summary>Every agent a picker may offer, sorted by codename; see <see cref="AgentSelection"/>.</summary>
    /// <remarks>Team leads and partners are never selectable, no matter which flags they carry.</remarks>
    Task<List<Agent>> GetSelectableAsync(CancellationToken cancellationToken = default);

    Task<Agent?> FindAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Free badge numbers, including the target's already assigned legacy value.</summary>
    Task<List<string>> GetAvailableBadgeNumbersAsync(string agentId, CancellationToken cancellationToken = default);

    /// <summary>Release a pending account and assign rank, division and the Administration Division flag; status becomes Active.</summary>
    /// <remarks>A full division needs <paramref name="confirmOverLimit"/>, else a <see cref="DivisionLimitExceededException"/> asks for it.</remarks>
    Task ReleaseAsync(string agentId, Rank rank, Division division, bool isHRB, bool confirmOverLimit, ClaimsPrincipal actor);

    /// <summary>Release a pending account as an external partner; no rank or internal flags, read-only on released records.</summary>
    Task ReleaseAsPartnerAsync(string agentId, PartnerAgency agency, PartnerRank partnerRank, ClaimsPrincipal actor);

    /// <summary>Reject a registration; status becomes Blocked with a reason.</summary>
    Task RejectAsync(string agentId, string reason, ClaimsPrincipal actor);

    /// <summary>Promote an applicant (status Applicant) directly to an active agent with rank, division and flag; leadership only. Rotates the SecurityStamp.</summary>
    Task PromoteApplicantToAgentAsync(string applicantUserId, Rank rank, Division division, bool isHRB, bool confirmOverLimit, ClaimsPrincipal actor);

    /// <summary>Turn a citizen account into an applicant so it can use the applicant portal; the citizen area stays open.</summary>
    Task<Agent> StartApplicationAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>Set an agent's master data; codename is required.</summary>
    Task MasterDataChangeAsync(string agentId, string? realName, string codename, string? badgeNumber, ClaimsPrincipal actor);

    /// <summary>Request a self master-data change (below Special Agent in Charge): staged until leadership approves; codename required. A repeat call overwrites a pending request.</summary>
    Task NameChangeRequestAsync(string agentId, string? realName, string codename, string? badgeNumber, ClaimsPrincipal actor);

    /// <summary>Pending name-change requests for the release inbox.</summary>
    Task<List<Agent>> GetPendingNameChangesAsync(CancellationToken cancellationToken = default);

    /// <summary>Approve the pending name-change request; applies staged values and ends sessions.</summary>
    Task NameChangeApproveAsync(string agentId, ClaimsPrincipal actor);

    /// <summary>Reject the pending name-change request; staged values are discarded.</summary>
    Task NameChangeRejectAsync(string agentId, string reason, ClaimsPrincipal actor);

    /// <summary>Set the own profile picture. Leadership applies instantly, everyone else stages it for release; a repeat call replaces a staged picture.</summary>
    Task AvatarSetAsync(string agentId, Stream content, string contentType, long sizeBytes, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Drop the profile picture including a staged one; own account, or any account for leadership (moderation).</summary>
    Task AvatarRemoveAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Staged profile pictures for the release inbox.</summary>
    Task<List<Agent>> GetPendingAvatarsAsync(CancellationToken cancellationToken = default);

    /// <summary>Approve the staged profile picture; replaces the active one.</summary>
    Task AvatarApproveAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Reject the staged profile picture; the active one stays.</summary>
    Task AvatarRejectAsync(string agentId, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Resolve the owner of a stored picture file; the serving endpoint's only way into the data.</summary>
    Task<Agent?> FindByAvatarFileAsync(string fileName, CancellationToken cancellationToken = default);

    /// <summary>Set the rank of an agent; which rank for whom is the fine gate of <see cref="PromotionRules"/>. Rotates the SecurityStamp.</summary>
    Task RankChangeAsync(string agentId, Rank rank, ClaimsPrincipal actor);

    /// <summary>Change a partner account's partner rank; refreshes the claims.</summary>
    Task SetPartnerRankAsync(string agentId, PartnerRank partnerRank, ClaimsPrincipal actor);

    /// <summary>Switch an active account to a partner agency (also used to change the agency of an existing
    /// partner). Clears the internal rank, division and Administration Division/team-lead/admin flags; forces re-login.</summary>
    Task ConvertToPartnerAsync(string agentId, PartnerAgency agency, PartnerRank partnerRank, ClaimsPrincipal actor);

    /// <summary>Switch an active partner account back to an internal FIB agent with the given rank (CID); forces re-login. Passes the fine rank gate like a first release.</summary>
    Task ConvertToInternalAsync(string agentId, Rank rank, ClaimsPrincipal actor);

    /// <summary>Decide a promotion request (Section Chief for the own division, Board or Admin for all; see <see cref="PromotionRules"/>); on approval sets the rank, logs it, and rotates the SecurityStamp.</summary>
    Task PromotionDecideAsync(string requestId, bool approved, string? note, ClaimsPrincipal actor);

    /// <summary>Move an agent to a division; Board or admin only. Writes the TRU flag with it and ends the agent's sessions.</summary>
    /// <remarks>A full division needs <paramref name="confirmOverLimit"/>, else a <see cref="DivisionLimitExceededException"/> asks for it.</remarks>
    Task DivisionSetAsync(string agentId, Division division, bool confirmOverLimit, ClaimsPrincipal actor);

    /// <summary>Set the Administration Division flag.</summary>
    Task HrbSetAsync(string agentId, bool isHRB, ClaimsPrincipal actor);
    Task AdminSetAsync(string agentId, bool isAdmin, ClaimsPrincipal actor);

    /// <summary>Mark/unmark an agent as FiveM team-lead; visibility marker only, grants no rights.</summary>
    Task TeamLeadSetAsync(string agentId, bool isTeamLead, ClaimsPrincipal actor);

    /// <summary>Kill-switch: status Blocked and end all sessions immediately.</summary>
    Task BlockAsync(string agentId, string reason, ClaimsPrincipal actor);

    /// <summary>Lift a block; status becomes Active. Refuses terminated agents - those go through the personnel file.</summary>
    Task UnblockAsync(string agentId, ClaimsPrincipal actor);

    /// <summary>Terminate an agent: status Terminated, admin rights dropped, permanent recruitment blacklist,
    /// sessions end at the next revalidation. Optionally files a personnel-file entry and posts a Discord embed.
    /// Leadership only; blocked for self, applicants, the last admin, and bootstrap admins.</summary>
    Task TerminateAsync(string agentId, string reason, bool createNote, bool postDiscord,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Revoke a termination: status Active, termination fields cleared, blacklist entry lifted.
    /// The personnel-file entry and the audit trail are kept.</summary>
    Task TerminationRevokeAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Hard-delete an agent: purge personnel/junction rows and drop the user (severs the Discord
    /// login, so a re-login creates a fresh Pending account). Keeps authored content and audit logs.
    /// Leadership only; blocked for self, the last admin, and bootstrap admins.</summary>
    Task DeleteAccountAsync(string agentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
