using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Effective unit of an internal account: the Board by rank, plus the home division it returns to.</summary>
public readonly record struct OrgUnit(Division? Home, bool IsBoard)
{
    /// <summary>No unit: partner, applicant or an account without a rank.</summary>
    public bool IsNone => Home is null && !IsBoard;
}

/// <summary>A soft seat limit would be exceeded without confirmation; the message is meant for the UI.</summary>
public sealed class DivisionLimitExceededException(string unitName, int taken, int limit)
    : InvalidOperationException(DivisionRules.LimitMessage(unitName, taken, limit))
{
    public int Taken { get; } = taken;
    public int Limit { get; } = limit;
}

/// <summary>Division membership, rank threshold and seat limits. Sole source for the guards and the pickers that mirror them.</summary>
public static class DivisionRules
{
    /// <summary>First rank on the Board.</summary>
    public const Rank BoardFloor = Rank.DeputyDirector;

    /// <summary>Lowest rank in the HRT; CID and CTD take every rank.</summary>
    public const Rank HrtMinimumRank = Rank.SpecialAgent;

    public static bool IsBoard(Rank? rank) => rank is { } r && r >= BoardFloor;

    /// <summary>Effective unit of an agent row.</summary>
    public static OrgUnit Of(Agent agent) => Of(agent.Rank, agent.Division, agent.IsTRU, agent.PartnerAgency is null);

    /// <summary>Effective unit from stored columns; an empty home is derived from the legacy TRU flag.</summary>
    public static OrgUnit Of(Rank? rank, Division? stored, bool isTru, bool internalAccount = true)
    {
        if (rank is null || !internalAccount)
        {
            return default;
        }
        return new OrgUnit(Normalize(rank, stored ?? (isTru ? Division.Hrt : Division.Cid)), IsBoard(rank));
    }

    /// <summary>Home division a rank may hold; none without a rank, CID by default or when the HRT is out of reach.</summary>
    public static Division? Normalize(Rank? rank, Division? requested)
    {
        if (rank is null)
        {
            return null;
        }
        var division = requested ?? Division.Cid;
        return division == Division.Hrt && rank < HrtMinimumRank ? Division.Cid : division;
    }

    /// <summary>Legacy TRU flag: HRT below the Board.</summary>
    public static bool IsTru(Rank? rank, Division? home) => home == Division.Hrt && !IsBoard(rank);

    public static bool IsTru(OrgUnit unit) => unit is { Home: Division.Hrt, IsBoard: false };

    /// <summary>Write division and TRU flag from the agent's rank; every rank or division write ends here.</summary>
    public static void Apply(Agent agent, Division? requested)
    {
        agent.Division = Normalize(agent.Rank, requested);
        agent.IsTRU = IsTru(agent.Rank, agent.Division);
    }

    /// <summary>Leave the divisions (partner).</summary>
    public static void Clear(Agent agent)
    {
        agent.Division = null;
        agent.IsTRU = false;
    }

    public static bool MayHold(Division division, Rank? rank)
        => rank is { } r && (division != Division.Hrt || r >= HrtMinimumRank);

    public static void RequireMayHold(Division division, Rank? rank)
    {
        if (!MayHold(division, rank))
        {
            throw new InvalidOperationException(HoldHint(division, rank));
        }
    }

    /// <summary>Why the rank cannot hold the division.</summary>
    public static string HoldHint(Division division, Rank? rank)
        => rank is null
            ? "Nur interne Konten mit Dienstgrad gehören einer Abteilung an."
            : $"{Capital(DivisionDisplay.WithArticle(division))} ist erst ab dem Dienstgrad {RankDisplay.DefaultName(HrtMinimumRank)} möglich.";

    public static int Limit(OrgUnit unit)
        => unit.IsBoard ? DivisionDisplay.BoardLimit : unit.Home is { } home ? DivisionDisplay.Limit(home) : 0;

    /// <summary>Short label for audit lines and chips.</summary>
    public static string Label(OrgUnit unit)
        => unit.IsBoard
            ? DivisionDisplay.BoardShort + (unit.Home is { } home ? $" ({DivisionDisplay.Short(home)})" : string.Empty)
            : unit.Home is { } division ? DivisionDisplay.Short(division) : "—";

    /// <summary>Same seat; a Board member holds one whatever the home division.</summary>
    public static bool SameSeat(OrgUnit a, OrgUnit b)
        => a.IsBoard == b.IsBoard && (a.IsBoard || a.Home == b.Home);

    /// <summary>Selectable agents seated in the unit, not counting the agent being moved.</summary>
    public static async Task<int> CountAsync(IQueryable<Agent> users, OrgUnit unit, string? exceptAgentId,
        CancellationToken cancellationToken = default)
    {
        // few rows, so the derived home is resolved in memory instead of in SQL
        var rows = await users.OnlySelectable().Where(a => a.Rank != null)
            .Select(a => new { a.Id, a.Rank, a.Division, a.IsTRU })
            .ToListAsync(cancellationToken);
        return rows.Count(r => r.Id != exceptAgentId && SameSeat(Of(r.Rank, r.Division, r.IsTRU), unit));
    }

    /// <summary>One more seat beyond the limit needs the explicit confirmation.</summary>
    public static void RequireSeat(OrgUnit unit, int takenByOthers, bool confirmOverLimit)
    {
        var limit = Limit(unit);
        if (takenByOthers + 1 > limit && !confirmOverLimit)
        {
            var name = unit.IsBoard ? DivisionDisplay.BoardWithArticle
                : unit.Home is { } home ? DivisionDisplay.WithArticle(home) : "—";
            throw new DivisionLimitExceededException(name, takenByOthers, limit);
        }
    }

    internal static string LimitMessage(string unitNameWithArticle, int taken, int limit)
        => $"{Capital(unitNameWithArticle)} ist mit {taken} von {limit} Plätzen bereits voll besetzt. "
           + "Das Limit lässt sich nur mit ausdrücklicher Bestätigung überschreiten.";

    private static string Capital(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
