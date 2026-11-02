using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>The rank floor of the leadership chart, internal and public alike.</summary>
/// <remarks>
/// A second axis on top of an already-authorised set, exactly like <c>GamificationService.LeadershipFloor</c> —
/// it does NOT belong in <c>AgentSelection</c>: putting it there would empty every picker in the house.
/// </remarks>
public static class LeadershipChart
{
    /// <summary>Supervisory Special Agent and up; the band the house calls leadership.</summary>
    public const Rank RankFloor = Rank.SpecialAgentInCharge;
}
