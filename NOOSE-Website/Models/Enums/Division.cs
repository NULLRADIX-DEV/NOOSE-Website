using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Home division of an internal agent; persisted as int. The Board is derived from the rank and has no value here.</summary>
public enum Division
{
    /// <summary>Criminal Investigation Division.</summary>
    Cid = 1,
    /// <summary>Counterterrorism Division.</summary>
    Ctd = 2,
    /// <summary>Hostage Rescue Team.</summary>
    Hrt = 3,
}

/// <summary>Display names, seat limits, icons and colours of the divisions and the Board.</summary>
public static class DivisionDisplay
{
    /// <summary>Seat limit of the Board of Directors.</summary>
    public const int BoardLimit = 4;

    public static string Name(Division division) => division switch
    {
        Division.Cid => "Criminal Investigation Division (CID)",
        Division.Ctd => "Counterterrorism Division (CTD)",
        Division.Hrt => "Hostage Rescue Team (HRT)",
        _ => "—",
    };

    public static string Short(Division division) => division switch
    {
        Division.Cid => "CID",
        Division.Ctd => "CTD",
        Division.Hrt => "HRT",
        _ => "—",
    };

    /// <summary>Name with its German article, lower case: "das Hostage Rescue Team (HRT)".</summary>
    public static string WithArticle(Division division) => division switch
    {
        Division.Cid => "die " + Name(division),
        Division.Ctd => "die " + Name(division),
        Division.Hrt => "das " + Name(division),
        _ => "—",
    };

    /// <summary>Seat limit; soft, exceeding it needs an explicit confirmation.</summary>
    public static int Limit(Division division) => division switch
    {
        Division.Cid => 21,
        Division.Ctd => 6,
        Division.Hrt => 9,
        _ => 0,
    };

    public static string Icon(Division division) => division switch
    {
        Division.Cid => Icons.Material.Filled.ManageSearch,
        Division.Ctd => Icons.Material.Filled.Shield,
        Division.Hrt => Icons.Material.Filled.LocalPolice,
        _ => Icons.Material.Filled.HelpOutline,
    };

    public static Color Colour(Division division) => division switch
    {
        Division.Cid => Color.Primary,
        Division.Ctd => Color.Tertiary,
        Division.Hrt => Color.Warning,
        _ => Color.Default,
    };

    public const string BoardName = "Board of Directors";
    public const string BoardShort = "Board";
    public const string BoardWithArticle = "das Board of Directors";
    public const string BoardIcon = Icons.Material.Filled.AccountBalance;
    public const Color BoardColour = Color.Secondary;

    /// <summary>All divisions in display order.</summary>
    public static readonly IReadOnlyList<Division> All = new[]
    {
        Division.Cid,
        Division.Ctd,
        Division.Hrt,
    };
}
