using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who acts: admin flag, rank and home division. A guard reads it from the row, the UI from the claims.</summary>
public readonly record struct PromotionStanding(bool IsAdmin, Rank? ActorRank, Division? Home)
{
    /// <summary>Board by rank alone; the admin flag makes no Board member.</summary>
    public bool IsBoard => DivisionRules.IsBoard(ActorRank);

    /// <summary>Sees and decides everything: Board or admin.</summary>
    public bool SeesAll => IsAdmin || IsBoard;

    /// <summary>Passes the coarse gate, the twin of <c>MayPromotionDecide</c>.</summary>
    public bool MayDecideAny => IsAdmin || ActorRank >= PromotionRules.DeciderFloor;

    /// <summary>From the claims; for buttons and pickers, never a guard.</summary>
    public static PromotionStanding Of(ClaimsPrincipal user)
    {
        var rank = user.GetRank();
        return new(user.IsAdmin(), rank, DivisionRules.Of(rank, user.GetDivision(), user.IsTRU()).Home);
    }

    /// <summary>From the actor's row; a row that is gone, inactive or external has no rank.</summary>
    public static PromotionStanding Of(ClaimsPrincipal user, Agent? row)
    {
        if (row is not { Status: AgentStatus.Active, PartnerAgency: null, Rank: not null })
        {
            return new(user.IsAdmin(), null, null);
        }
        return new(user.IsAdmin(), row.Rank, DivisionRules.Of(row).Home);
    }

    /// <summary>From a row alone, for a caller without a principal; the admin flag is the row's.</summary>
    public static PromotionStanding Of(Agent? row)
    {
        if (row is not { Status: AgentStatus.Active, PartnerAgency: null, Rank: not null })
        {
            return new(row?.IsAdmin == true && row.Status == AgentStatus.Active, null, null);
        }
        return new(row.IsAdmin, row.Rank, DivisionRules.Of(row).Home);
    }

    /// <summary>The actor's standing fresh from the database; claims are up to 30 seconds old.</summary>
    public static async Task<PromotionStanding> ReadAsync(IQueryable<Agent> users, ClaimsPrincipal user,
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

/// <summary>Who may hand out which rank to whom; the guards and the buttons mirroring them share it.</summary>
/// <remarks>Board and admin assign anything; a Section Chief inside the own division below Section Chief; a Special Agent in Charge only the first release as Trainee into the CID.</remarks>
public static class PromotionRules
{
    /// <summary>Lowest rank that passes the coarse gate.</summary>
    public const Rank DeciderFloor = Rank.SectionChief;

    /// <summary>A Section Chief assigns only ranks below this, to agents below it.</summary>
    public const Rank SectionChiefCeiling = Rank.SectionChief;

    /// <summary>The one rank a Special Agent in Charge releases a new account as.</summary>
    public const Rank FirstReleaseRank = Rank.Trainee;

    /// <summary>The one division a Special Agent in Charge releases into.</summary>
    public const Division FirstReleaseDivision = Division.Cid;

    /// <summary>May the actor give a target in <paramref name="targetDivision"/> the rank <paramref name="toRank"/>?</summary>
    /// <param name="fromRank">Current rank of the target; null for a first release.</param>
    public static bool MayAssign(PromotionStanding actor, Division? targetDivision, Rank? fromRank, Rank toRank)
    {
        if (actor.SeesAll)
        {
            return true;
        }
        return actor.ActorRank switch
        {
            Rank.SectionChief => actor.Home is { } home && targetDivision == home
                && toRank < SectionChiefCeiling && (fromRank is null || fromRank < SectionChiefCeiling),
            Rank.SpecialAgentInCharge => fromRank is null && toRank == FirstReleaseRank
                && targetDivision == FirstReleaseDivision,
            _ => false,
        };
    }

    /// <summary>Why <see cref="MayAssign"/> says no; null when it says yes.</summary>
    public static string? Refusal(PromotionStanding actor, Division? targetDivision, Rank? fromRank, Rank toRank)
    {
        if (MayAssign(actor, targetDivision, fromRank, toRank))
        {
            return null;
        }
        var ceiling = RankDisplay.DefaultName(SectionChiefCeiling);
        switch (actor.ActorRank)
        {
            case Rank.SectionChief when toRank >= SectionChiefCeiling || fromRank >= SectionChiefCeiling:
                return $"Dienstgrade ab {ceiling} vergeben nur das Board oder ein Admin.";
            case Rank.SectionChief:
                return $"Als {ceiling} vergibst du Dienstgrade nur in deiner eigenen Abteilung.";
            case Rank.SpecialAgentInCharge:
                return $"Ein {RankDisplay.DefaultName(Rank.SpecialAgentInCharge)} gibt neue Konten nur als "
                       + $"{RankDisplay.DefaultName(FirstReleaseRank)} in {DivisionDisplay.WithArticle(FirstReleaseDivision)} frei.";
            default:
                return $"Dienstgrade vergeben das Board, Admins und in der eigenen Abteilung ein {ceiling}.";
        }
    }

    /// <summary>The guard every rank write passes after its write guard.</summary>
    public static void RequireMayAssign(PromotionStanding actor, Division? targetDivision, Rank? fromRank, Rank toRank)
    {
        if (Refusal(actor, targetDivision, fromRank, toRank) is { } message)
        {
            throw new UnauthorizedAccessException(message);
        }
    }

    /// <summary>The division an actor without a division picker releases into: the own one for a Section Chief, else the CID.</summary>
    public static Division ReleaseDivision(PromotionStanding actor)
        => actor.ActorRank == Rank.SectionChief && actor.Home is { } home ? home : FirstReleaseDivision;

    /// <summary>The ranks a picker may offer for this target; empty when the actor assigns none.</summary>
    public static IReadOnlyList<Rank> AssignableRanks(PromotionStanding actor, Division? targetDivision, Rank? fromRank)
        => RankDisplay.All.Where(r => MayAssign(actor, targetDivision, fromRank, r)).ToList();

    /// <summary>May the actor decide a request for <paramref name="toRank"/> on this agent? One button per row.</summary>
    public static bool MayDecide(PromotionStanding actor, Agent? target, Rank toRank)
        => actor.MayDecideAny && target is not null && MayAssign(actor, DivisionRules.Of(target).Home, target.Rank, toRank);

    /// <summary>Does the actor's request list show this target? Board and admin all, a Section Chief the own division.</summary>
    public static bool MaySee(PromotionStanding actor, Agent? target)
    {
        if (!actor.MayDecideAny || target is null)
        {
            return false;
        }
        if (actor.SeesAll)
        {
            return true;
        }
        var unit = DivisionRules.Of(target);
        return actor.ActorRank == Rank.SectionChief && actor.Home is { } home && !unit.IsBoard && unit.Home == home;
    }

    /// <summary>Open requests the actor may see, oldest first; the one source of list, badge and dashboard.</summary>
    public static async Task<List<AgentPromotionRequest>> OpenRequestsAsync(AppDbContext db, PromotionStanding actor,
        CancellationToken cancellationToken = default)
    {
        if (!actor.MayDecideAny)
        {
            return [];
        }
        var open = await db.AgentPromotionRequests.AsNoTracking()
            .Where(a => a.Status == PromotionStatus.Requested)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);
        if (actor.SeesAll || open.Count == 0)
        {
            return open;
        }
        // few rows, home resolved in memory
        var ids = open.Select(a => a.AgentId).Distinct().ToList();
        var targets = await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
        return open.Where(r => MaySee(actor, targets.FirstOrDefault(t => t.Id == r.AgentId))).ToList();
    }

    /// <summary>Number of open requests the actor may see.</summary>
    public static async Task<int> CountOpenAsync(AppDbContext db, PromotionStanding actor,
        CancellationToken cancellationToken = default)
        => (await OpenRequestsAsync(db, actor, cancellationToken)).Count;
}
