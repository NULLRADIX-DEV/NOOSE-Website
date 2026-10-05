using System.Security.Claims;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Authorization;

/// <summary>Typed access to the NOOSE claims of a signed-in agent.</summary>
public static class AgentPrincipalExtensions
{
    /// <summary>Identity key (GUID) of the agent, or null if not signed in.</summary>
    public static string? GetAgentId(this ClaimsPrincipal user)
        => user.FindFirstValue(ClaimTypes.NameIdentifier);

    /// <summary>Codename, visible to all users.</summary>
    public static string? GetCodename(this ClaimsPrincipal user)
        => user.FindFirstValue(AgentClaimTypes.Codename);

    /// <summary>Badge number, visible to all users.</summary>
    public static string? GetBadgeNumber(this ClaimsPrincipal user)
    {
        var value = user.FindFirstValue(AgentClaimTypes.BadgeNumber);
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    public static Rank? GetRank(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(AgentClaimTypes.Rank);
        return int.TryParse(raw, out var value) && Enum.IsDefined(typeof(Rank), value)
            ? (Rank)value
            : null;
    }

    public static AgentStatus? GetStatus(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(AgentClaimTypes.Status);
        return Enum.TryParse<AgentStatus>(raw, out var value) ? value : null;
    }

    public static bool IsAdmin(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsAdmin), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Configured bootstrap admin (Discord ID in Bootstrap config); gate for demo mode and demo data.</summary>
    public static bool IsBootstrapAdmin(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsBootstrap), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Configured AI owner (Discord ID in Ki config); sole gate for changing NOOSEI quotas.</summary>
    public static bool IsAiOwner(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsAiOwner), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsTRU(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsTRU), "true", StringComparison.OrdinalIgnoreCase);

    public static bool IsHRB(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsHRB), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>TeamLead marker (FiveM supervision); on its own grants nothing, only forms read-only supervision when combined with no admin flag.</summary>
    public static bool IsTeamLead(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsTeamLead), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read-only public demo visitor (synthetic principal); reads everything, writes nothing.</summary>
    public static bool IsDemo(this ClaimsPrincipal user)
        => string.Equals(user.FindFirstValue(AgentClaimTypes.IsDemo), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>Read-only supervision = TeamLead without admin: reads everything but writes nothing and never sees real names.</summary>
    public static bool IsOnlyReader(this ClaimsPrincipal user)
        => user.IsTeamLead() && !user.IsAdmin();

    /// <summary>External partner agency from claim, or null for internal NOOSE agents.</summary>
    public static PartnerAgency? GetPartnerAgency(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(AgentClaimTypes.PartnerAgency);
        return int.TryParse(raw, out var value) && Enum.IsDefined(typeof(PartnerAgency), value)
            ? (PartnerAgency)value
            : null;
    }

    /// <summary>Partner rank tier from claim, or null for internal NOOSE agents.</summary>
    public static PartnerRank? GetPartnerRank(this ClaimsPrincipal user)
    {
        var raw = user.FindFirstValue(AgentClaimTypes.PartnerRank);
        return int.TryParse(raw, out var value) && Enum.IsDefined(typeof(PartnerRank), value)
            ? (PartnerRank)value
            : null;
    }

    /// <summary>External partner (DoJ/LSPD/LSMD): read-only, sees only released non-classified content.</summary>
    public static bool IsPartner(this ClaimsPrincipal user) => user.GetPartnerAgency() is not null;

    /// <summary>True if the viewer is a partner of the given agency at or above the given rank tier.</summary>
    public static bool HasPartnerRank(this ClaimsPrincipal user, PartnerAgency agency, PartnerRank minimum)
        => user.GetPartnerAgency() == agency && user.GetPartnerRank() is { } rank && rank >= minimum;

    /// <summary>May write at all; false for read-only supervisors, partners and demo visitors. Sole source for write-control visibility.</summary>
    public static bool MayWrite(this ClaimsPrincipal user) => !user.IsOnlyReader() && !user.IsPartner() && !user.IsDemo();

    /// <summary>May contribute content (post taskforce chat, author documents) = internal writers plus partner contributors, never read-only supervision or demo.</summary>
    public static bool MayContribute(this ClaimsPrincipal user) => user.MayWrite() || user.IsPartner();

    /// <summary>Leadership = rank ≥ Supervisory Special Agent or admin.</summary>
    public static bool IsLeadership(this ClaimsPrincipal user)
        => user.IsAdmin() || user.GetRank() is >= Rank.SupervisorySpecialAgent;

    /// <summary>May READ classified content = leadership or read-only supervision. Read gates only, never write/real-name.</summary>
    public static bool MayClassifiedRead(this ClaimsPrincipal user)
        => user.IsLeadership() || user.IsOnlyReader();

    /// <summary>May see ALL taskforces (without assignment) = leadership or read-only supervision. Sole source of this rule.</summary>
    public static bool MayAllTaskforcesSee(this ClaimsPrincipal user)
        => user.IsLeadership() || user.IsOnlyReader();

    /// <summary>May see the otherwise-hidden real name = leadership/admin but never read-only supervision. Sole source of the real-name rule.</summary>
    public static bool MayRealNameSee(this ClaimsPrincipal user) => user.IsLeadership() && !user.IsOnlyReader();

    /// <summary>May use the counter-intelligence cockpit = leadership but never read-only supervision (it would audit itself). Sole source of this rule.</summary>
    public static bool MayCounterIntel(this ClaimsPrincipal user) => user.IsLeadership() && !user.IsOnlyReader();

    /// <summary>May set "secured state-threatening" directly = rank ≥ Senior Special Agent or admin.</summary>
    public static bool MayHighestClassification(this ClaimsPrincipal user)
        => user.IsAdmin() || user.GetRank() is >= Rank.SeniorSpecialAgent;

    /// <summary>May take something out of the evidence room = rank ≥ Senior Special Agent or admin. Sole source of this rule.</summary>
    public static bool MayEvidenceWithdraw(this ClaimsPrincipal user)
        => user.IsAdmin() || user.GetRank() is >= Rank.SeniorSpecialAgent;

    /// <summary>May READ meeting agendas and minutes = highest-classification rank or read-only supervision. Sole source of this rule.</summary>
    public static bool MayMeetingRead(this ClaimsPrincipal user)
        => user.MayHighestClassification() || user.IsOnlyReader();

    /// <summary>May decide promotions = rank ≥ Deputy Director or admin.</summary>
    public static bool MayPromotionDecide(this ClaimsPrincipal user)
        => user.IsAdmin() || user.GetRank() is >= Rank.DeputyDirector;

    /// <summary>Public applicant (status Applicant): portal-only access, never an internal agent.</summary>
    public static bool IsApplicant(this ClaimsPrincipal user) => user.GetStatus() == AgentStatus.Applicant;

    /// <summary>May act in the applicant portal: an applicant, or a partner applying without giving up partner access.</summary>
    /// <remarks>
    /// A partner keeps status Active while applying — the application is a row of its own, not an account state — so
    /// the portal cannot gate on <see cref="IsApplicant"/> alone. Mirrors the partner carve-out of
    /// <c>ReadOnlyBarrierInterceptor</c> (partner, but neither supervision nor demo), so guard and interceptor agree.
    /// </remarks>
    public static bool MayApply(this ClaimsPrincipal user)
        => user.IsApplicant() || (user.IsPartner() && !user.IsOnlyReader() && !user.IsDemo());

    /// <summary>May access recruiting management = HRB member or leadership.</summary>
    public static bool IsHrbOrLeadership(this ClaimsPrincipal user) => user.IsHRB() || user.IsLeadership();

    /// <summary>Citizen (status Civilian): public-area account only, never an agent and never an applicant.</summary>
    public static bool IsCitizen(this ClaimsPrincipal user) => user.GetStatus() == AgentStatus.Civilian;

    /// <summary>May enter the citizen area = every signed-in account; Civilian only means the account has nothing else.</summary>
    public static bool MayUseCitizenPortal(this ClaimsPrincipal user) => user.Identity?.IsAuthenticated == true;

    /// <summary>May act as a private person in the citizen area (civilian identity, ticket, tip) = every signed-in account; only the synthetic demo visitor is out. Sole source of this rule.</summary>
    /// <remarks>
    /// Partner and read-only supervision are in on purpose, unlike <see cref="MayWrite"/>: behind both accounts sits
    /// a person who plays a civilian on the server, and what they file out of that identity is not a write into the
    /// record stock. The demo visitor is the one exclusion, because it is nobody: a synthetic principal shared by
    /// every anonymous guest, so its "own" tickets would be everyone's.
    /// </remarks>
    public static bool MayCitizenSubmit(this ClaimsPrincipal user)
        => user.MayUseCitizenPortal() && !user.IsDemo();

    /// <summary>An in-house account: released, not external. The read-only supervision is one, a partner is not.</summary>
    /// <remarks>
    /// Active alone does the work of four exclusions — Pending, Blocked, Applicant and Civilian all fail it — so a
    /// caller does not have to remember which of them still carries an agent id.
    /// </remarks>
    public static bool IsInternalAgent(this ClaimsPrincipal user)
        => user.GetStatus() == AgentStatus.Active
           && !user.IsPartner()
           && !string.IsNullOrEmpty(user.GetAgentId());
}
