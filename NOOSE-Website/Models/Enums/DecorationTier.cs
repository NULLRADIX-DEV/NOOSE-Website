namespace NOOSE_Website.Models.Enums;

/// <summary>Colour grade of a decoration.</summary>
public enum DecorationTier
{
    Gold = 0,
    Silver = 1,
    Bronze = 2,
    Blue = 3,
    Red = 4,
    Green = 5,
}

/// <summary>Labels and colours of a decoration grade.</summary>
public static class DecorationTierDisplay
{
    /// <summary>Every grade, highest first; also the sort order of plaques.</summary>
    public static readonly IReadOnlyList<DecorationTier> All = new[]
    {
        DecorationTier.Gold,
        DecorationTier.Silver,
        DecorationTier.Bronze,
        DecorationTier.Blue,
        DecorationTier.Red,
        DecorationTier.Green,
    };

    public static string Name(DecorationTier tier) => tier switch
    {
        DecorationTier.Gold => "Gold",
        DecorationTier.Silver => "Silber",
        DecorationTier.Bronze => "Bronze",
        DecorationTier.Blue => "Blau",
        DecorationTier.Red => "Rot",
        DecorationTier.Green => "Grün",
        _ => "—",
    };

    /// <summary>Fill colour of the plaque and of the symbol on a certificate.</summary>
    public static string Hex(DecorationTier tier) => tier switch
    {
        DecorationTier.Gold => "#C9A227",
        DecorationTier.Silver => "#9AA4AE",
        DecorationTier.Bronze => "#B06E36",
        DecorationTier.Blue => "#2F6FD6",
        DecorationTier.Red => "#C0392B",
        DecorationTier.Green => "#2E8B57",
        _ => "#9AA4AE",
    };
}
