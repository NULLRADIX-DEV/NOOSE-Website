namespace NOOSE_Website.Models.Enums;

/// <summary>Who owes a paragraph of the service regulation: the whole bureau or one unit. Stored as int in <c>Zielgruppe</c>, never renumber.</summary>
public enum RegulationAudience
{
    /// <summary>Every agent who may see it.</summary>
    Bureau = 0,
    /// <summary>Carriers of the Administration Division flag, across the divisions and the Board.</summary>
    Administration = 1,
    /// <summary>Seated in the CID; the Board is its own seat.</summary>
    Cid = 2,
    /// <summary>Seated in the CTD.</summary>
    Ctd = 3,
    /// <summary>Seated in the HRT.</summary>
    Hrt = 4,
}

/// <summary>Labels of the audience; one place for page, editor, print and trash.</summary>
public static class RegulationAudienceDisplay
{
    /// <summary>Picker order: the bureau first, then the units as the organisation chart lists them.</summary>
    public static readonly IReadOnlyList<RegulationAudience> All =
    [
        RegulationAudience.Bureau, RegulationAudience.Administration,
        RegulationAudience.Cid, RegulationAudience.Ctd, RegulationAudience.Hrt,
    ];

    /// <summary>The division behind a division audience; null for the bureau and the Administration Division.</summary>
    public static Division? DivisionOf(RegulationAudience audience) => audience switch
    {
        RegulationAudience.Cid => Division.Cid,
        RegulationAudience.Ctd => Division.Ctd,
        RegulationAudience.Hrt => Division.Hrt,
        _ => null,
    };

    /// <summary>The division audience of a home division.</summary>
    public static RegulationAudience Of(Division division) => division switch
    {
        Division.Cid => RegulationAudience.Cid,
        Division.Ctd => RegulationAudience.Ctd,
        Division.Hrt => RegulationAudience.Hrt,
        _ => throw new ArgumentOutOfRangeException(nameof(division)),
    };

    /// <summary>Full name for the picker and the detail page.</summary>
    public static string Label(RegulationAudience audience) => audience switch
    {
        RegulationAudience.Bureau => "Ganzes FIB",
        RegulationAudience.Administration => "Administration Division",
        _ when DivisionOf(audience) is { } division => DivisionDisplay.Name(division),
        _ => "—",
    };

    /// <summary>Short chip label; the bureau shows no chip.</summary>
    public static string Short(RegulationAudience audience) => audience switch
    {
        RegulationAudience.Bureau => "FIB",
        RegulationAudience.Administration => "Administration",
        _ when DivisionOf(audience) is { } division => DivisionDisplay.Short(division),
        _ => "—",
    };
}
