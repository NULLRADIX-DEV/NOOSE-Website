using System.Security.Claims;
using NOOSE_Website.Authorization;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Read-visibility context: internal agent or external partner (released, non-classified only).</summary>
/// <param name="IsAdmin">Trailing with a default so every existing construction site still compiles. A scope built
/// without it fails closed: the document access-exclusion carve-out is the only reader, and false means the
/// exclusion applies.</param>
/// <param name="Rank">Service rank, for the audiences that are graded by it. Trailing and nullable for the same
/// reason, and null fails closed: a rank-addressed announcement stays hidden rather than reaching everyone.</param>
/// <param name="MayRealName">Whether an agent's real name may be shown. Its own field because the rule subtracts
/// the read-only supervision from leadership, which no other flag here expresses — see
/// <see cref="Authorization.AgentPrincipalExtensions.MayRealNameSee" />. False fails closed to the codename.</param>
/// <param name="IsInternalAgent">An in-house account. Its own field because <c>!IsPartner</c> is WIDER than the
/// rule — a citizen and an applicant are not partners either — so the audiences addressed as "every internal agent"
/// cannot be expressed without it. Trailing and defaulted false, which fails closed.</param>
/// <param name="MayAdministration">Opens the Administration workspace (division, Board, admin, supervision). Defaulted false.</param>
/// <param name="IsAdministrationMember">Works in it: the same without the supervision. Defaulted false.</param>
public readonly record struct ViewerScope(
    bool MayClassifiedRead, bool MayAllTaskforces, string? MeId, PartnerAgency? PartnerAgency,
    bool IsTru = false, bool IsHrb = false, bool IsLeadership = false, bool MayAgenda = false,
    bool IsAdmin = false, Rank? Rank = null, bool MayRealName = false, bool IsInternalAgent = false,
    bool MayAdministration = false, bool IsAdministrationMember = false)
{
    /// <summary>External partner viewer.</summary>
    public bool IsPartner => PartnerAgency is not null;

    /// <summary>May see recruiting (applications): HRB or leadership, never read-only supervision (which must not see real names).</summary>
    public bool MayRecruiting => IsLeadership || IsHrb;

    /// <summary>Build from the current principal.</summary>
    public static ViewerScope From(ClaimsPrincipal user)
        => new(user.MayClassifiedRead(), user.MayAllTaskforcesSee(), user.GetAgentId(), user.GetPartnerAgency(),
               user.IsTRU(), user.IsHRB(), user.IsLeadership(), user.MayMeetingRead(), user.IsAdmin(),
               user.GetRank(), user.MayRealNameSee(), user.IsInternalAgent(),
               user.MayAdministrationRead(), user.IsAdministrationMember());

    /// <summary>The same viewer as the document library sees them. Two scopes exist because the library reads the
    /// three secrecy flags differently; this is the bridge, not a merge.</summary>
    public DocumentViewerScope AsDocumentScope()
        => new(MayClassifiedRead, IsTru, IsHrb, IsLeadership, IsAdmin, MeId);

    /// <summary>True if this viewer may see a record at the given secrecy level.</summary>
    public bool CanSee(DocumentClassification level) => level switch
    {
        DocumentClassification.None => true,
        DocumentClassification.Leadership => MayClassifiedRead,
        DocumentClassification.Tru => MayClassifiedRead || IsTru,
        DocumentClassification.Hrb => MayClassifiedRead || IsHrb,
        _ => false,
    };
}
