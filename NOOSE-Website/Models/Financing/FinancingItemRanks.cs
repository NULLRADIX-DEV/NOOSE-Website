using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Financing;

/// <summary>Minimum rank of a catalog position per division; sole source for the request gate and the pickers.</summary>
public static class FinancingItemRanks
{
    /// <summary>Threshold for the home division; no division (or CID) reads the base value.</summary>
    public static Rank For(FinancingItem item, Division? division) => division switch
    {
        Division.Ctd => item.MinimumRankCtd ?? item.MinimumRank,
        Division.Hrt => item.MinimumRankHrt ?? item.MinimumRank,
        _ => item.MinimumRank,
    };

    /// <summary>Whether an agent of that rank and home division may request the position.</summary>
    public static bool MayRequest(FinancingItem item, Rank? rank, Division? division)
        => rank is { } r && For(item, division) <= r;

    /// <summary>One rank when all divisions agree, otherwise one entry per division (HRT with its own titles).</summary>
    public static string Label(FinancingItem item)
    {
        var ranks = DivisionDisplay.All.Select(d => (Division: d, Rank: For(item, d))).ToList();
        if (ranks.All(r => r.Rank == item.MinimumRank))
        {
            return RankDisplay.Name(item.MinimumRank);
        }
        return string.Join(" · ", ranks.Select(r => $"{DivisionDisplay.Short(r.Division)}: {RankDisplay.Name(r.Rank, r.Division)}"));
    }
}
