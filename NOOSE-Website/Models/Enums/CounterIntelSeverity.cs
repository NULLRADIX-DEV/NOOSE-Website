using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>How loud a counter-intelligence rule shouts when it triggers.</summary>
public enum CounterIntelSeverity
{
    Info = 0,
    Warning = 1,
    High = 2,
    Critical = 3,
}

/// <summary>Display labels.</summary>
public static class CounterIntelSeverityDisplay
{
    public static string Name(CounterIntelSeverity severity) => severity switch
    {
        CounterIntelSeverity.Info => "Hinweis",
        CounterIntelSeverity.Warning => "Auffällig",
        CounterIntelSeverity.High => "Hoch",
        CounterIntelSeverity.Critical => "Kritisch",
        _ => severity.ToString(),
    };

    /// <summary>Chip colour; High and Critical share the alarm colour.</summary>
    public static Color Colour(CounterIntelSeverity severity) => severity switch
    {
        CounterIntelSeverity.Info => Color.Info,
        CounterIntelSeverity.Warning => Color.Warning,
        _ => Color.Error,
    };

    /// <summary>Alert tone for a list whose worst finding has this level.</summary>
    public static Severity Alert(CounterIntelSeverity severity) => severity switch
    {
        CounterIntelSeverity.Info => Severity.Info,
        CounterIntelSeverity.Warning => Severity.Warning,
        _ => Severity.Error,
    };

    /// <summary>All levels, mildest first.</summary>
    public static readonly IReadOnlyList<CounterIntelSeverity> All =
    [
        CounterIntelSeverity.Info,
        CounterIntelSeverity.Warning,
        CounterIntelSeverity.High,
        CounterIntelSeverity.Critical,
    ];
}
