using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who acts on the service regulation: admin flag, rank, home division and Administration flag.</summary>
public readonly record struct RegulationStanding(bool IsAdmin, Rank? ActorRank, Division? Home, bool IsHrb)
{
    /// <summary>Board by rank alone; the admin flag makes no Board member.</summary>
    public bool IsBoard => DivisionRules.IsBoard(ActorRank);

    /// <summary>Edits every paragraph and the sections: Board or admin.</summary>
    public bool SeesAll => IsAdmin || IsBoard;

    /// <summary>From the claims; for buttons and pickers, never a guard.</summary>
    public static RegulationStanding Of(ClaimsPrincipal user)
    {
        var rank = user.GetRank();
        return new(user.IsAdmin(), rank, DivisionRules.Of(rank, user.GetDivision(), user.IsTRU()).Home, user.IsHRB());
    }

    /// <summary>From the actor's row; a row that is gone, inactive or external has no rights, and a revoked admin flag counts at once.</summary>
    public static RegulationStanding Of(ClaimsPrincipal user, Agent? row)
        => row is { Status: AgentStatus.Active, PartnerAgency: null }
            ? new(user.IsAdmin() && row.IsAdmin, row.Rank, row.Rank is null ? null : DivisionRules.Of(row).Home, row.IsHRB)
            : new(false, null, null, false);

    /// <summary>The actor's standing fresh from the database; claims are up to 30 seconds old.</summary>
    public static async Task<RegulationStanding> ReadAsync(IQueryable<Agent> users, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        var id = user.GetAgentId();
        if (string.IsNullOrEmpty(id))
        {
            return Of(user, null);
        }
        return Of(user, await users.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, cancellationToken));
    }
}

/// <summary>Limits, validity, audience and permissions of the service regulation; pure, so service and pages read one answer.</summary>
public static class RegulationRules
{
    public const int MaxHeading = 200;
    public const int MaxSectionTitle = 200;
    public const int MaxChangeNote = 300;

    /// <summary>"Abschnitt 3" for the third numbered section, "Vorwort" for the foreword.</summary>
    public static string SectionLabel(int? number) => number is { } n ? $"Abschnitt {n}" : "Vorwort";

    /// <summary>Section label and title as the regulation prints it: "Abschnitt 3: Handlungsvorschriften".</summary>
    public static string SectionCaption(int? number, string title)
        => number is null ? title : $"{SectionLabel(number)}: {title}";

    /// <summary>How a paragraph is cited in links, mentions and the bell: "DVO Abschnitt 3 · Funkdisziplin".</summary>
    public static string ParagraphLabel(int? sectionNumber, string heading)
        => $"DVO {SectionLabel(sectionNumber)} · {heading}";

    /// <summary>The newest version that has started by <paramref name="today"/>, or null when none has.</summary>
    public static int? ValidVersion(IReadOnlyList<(int Version, DateOnly ValidFrom)> versions, DateOnly today)
    {
        int? best = null;
        foreach (var (version, validFrom) in versions)
        {
            if (validFrom <= today && (best is null || version > best))
            {
                best = version;
            }
        }
        return best;
    }

    /// <summary>A new version may not start before the one it replaces.</summary>
    public static bool IsValidFromAllowed(DateOnly validFrom, DateOnly? previousValidFrom)
        => previousValidFrom is null || validFrom >= previousValidFrom;

    /// <summary>The first version an acknowledgment must reach: only a version that asks moves it.</summary>
    public static int NextAcknowledgeFrom(int currentAcknowledgeFrom, int newVersion, bool requiresAcknowledgment)
        => requiresAcknowledgment ? newVersion : currentAcknowledgeFrom;

    /// <summary>Coarse gate for every write: leadership with write access, never a team lead.</summary>
    public static bool MayManage(ClaimsPrincipal actor)
        // team leads stay invisible
        => actor.MayWrite() && actor.IsInternalAgent() && !actor.IsTeamLead() && actor.IsLeadership();

    /// <summary>Fine gate of a paragraph: Board and admin every one, a Section Chief only the audience of the own unit.</summary>
    public static bool MayEditParagraph(RegulationStanding actor, RegulationAudience audience)
    {
        if (actor.SeesAll)
        {
            return true;
        }
        if (actor.ActorRank != Rank.SectionChief)
        {
            return false;
        }
        return audience switch
        {
            RegulationAudience.Administration => actor.IsHrb,
            _ when RegulationAudienceDisplay.DivisionOf(audience) is { } division => actor.Home == division,
            _ => false,
        };
    }

    /// <summary>Sections are the regulation's frame: only Board and admin add, rename, move or remove them.</summary>
    public static bool MayManageSections(RegulationStanding actor) => actor.SeesAll;

    /// <summary>Audiences the actor may pick in the editor.</summary>
    public static IReadOnlyList<RegulationAudience> EditableAudiences(RegulationStanding actor)
        => RegulationAudienceDisplay.All.Where(a => MayEditParagraph(actor, a)).ToList();

    /// <summary>Why the actor may not edit a paragraph of that audience.</summary>
    public static string EditRefusal(RegulationAudience audience)
        => audience == RegulationAudience.Bureau
            ? "Absätze für das ganze FIB ändern nur das Board und Admins."
            : $"Absätze für {RegulationAudienceDisplay.Label(audience)} ändern das Board, Admins und der Section Chief dieser Einheit.";

    /// <summary>May see the quota and the list of who is missing: leadership and supervision, never a partner.</summary>
    public static bool MaySeeQuota(ClaimsPrincipal actor)
        => actor.MayClassifiedRead() && actor.IsInternalAgent();

    /// <summary>May acknowledge a paragraph: every internal agent that may write.</summary>
    public static bool MayAcknowledge(ClaimsPrincipal actor)
        => actor.MayWrite() && actor.IsInternalAgent();

    /// <summary>May read the regulation: every internal agent, never a partner.</summary>
    public static bool MayRead(ViewerScope scope) => scope.IsInternalAgent && !scope.IsPartner;

    /// <summary>In-memory twin of the debt query's audience test; a test holds both to <see cref="DivisionRules.Of(Agent)"/>.</summary>
    public static bool InAudience(RegulationAudience audience, OrgUnit unit, bool isHrb) => audience switch
    {
        RegulationAudience.Bureau => true,
        RegulationAudience.Administration => isHrb,
        _ when RegulationAudienceDisplay.DivisionOf(audience) is { } division => !unit.IsBoard && unit.Home == division,
        _ => false,
    };
}
