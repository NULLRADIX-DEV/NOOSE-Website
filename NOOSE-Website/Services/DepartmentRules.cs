using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Who may carry the Administration Division flag. Sole source for the guard and for the switches that mirror it.</summary>
/// <remarks>
/// Service regulation §2.4.1: joining the Administration Division is possible from the rank of Special Agent
/// upwards. The rule is about the target agent's rank, not about the acting one, so it
/// does not belong in <see cref="Permission"/> - and it lives here rather than inline in the service because the
/// admin switches have to disable on exactly the same predicate. A switch that stays enabled while the service
/// refuses hands the operator a dialog that fails on save.
/// </remarks>
public static class DepartmentRules
{
    /// <summary>Lowest rank that may hold the flag.</summary>
    public const Rank MinimumRank = Rank.SpecialAgent;

    /// <summary>Flag name with article, as guard and hints write it.</summary>
    public const string Name = "Die Administration Division";

    /// <summary>Column header and switch label.</summary>
    public const string ColumnLabel = "Administration";

    /// <summary>True when that rank may carry the department flag. A rankless account never may.</summary>
    public static bool MayHold(Rank? rank) => rank is { } r && r >= MinimumRank;

    /// <summary>Why a rank cannot carry the flag; the guard throws it, the switches show it.</summary>
    public static string HoldHint(string department)
        => $"{department} ist laut Dienstverordnung erst ab dem Dienstgrad {RankDisplay.DefaultName(MinimumRank)} möglich.";

    /// <summary>Tooltip of a switch that <see cref="MayHold"/> keeps off.</summary>
    public static string Hint => HoldHint(Name);

    /// <summary>Guard for a write that switches the department flag on.</summary>
    /// <remarks>Clearing is always allowed - a demoted account must not stay stuck with a flag it may not hold.</remarks>
    public static void RequireMayHold(Rank? rank, string department)
    {
        if (!MayHold(rank))
        {
            throw new InvalidOperationException(HoldHint(department));
        }
    }
}
