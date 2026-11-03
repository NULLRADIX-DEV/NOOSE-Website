namespace NOOSE_Website.Models.Enums;

/// <summary>Who a radio channel belongs to.</summary>
public enum RadioScope
{
    Noose = 1,
    Partner = 2,
    Other = 3,
}

/// <summary>Display labels for the radio plan's blocks.</summary>
public static class RadioScopeDisplay
{
    public static string Name(RadioScope scope) => scope switch
    {
        RadioScope.Noose => "FIB",
        RadioScope.Partner => "Partnerbehörde",
        RadioScope.Other => "Sonstige",
        _ => "—",
    };

    /// <summary>Heading above a block of channels.</summary>
    public static string Heading(RadioScope scope) => scope switch
    {
        RadioScope.Noose => "Eigene Kanäle",
        RadioScope.Partner => "Partnerbehörden",
        RadioScope.Other => "Sonstige Kanäle",
        _ => "—",
    };

    /// <summary>Blocks in plan order.</summary>
    public static readonly IReadOnlyList<RadioScope> All = new[]
    {
        RadioScope.Noose,
        RadioScope.Partner,
        RadioScope.Other,
    };
}
