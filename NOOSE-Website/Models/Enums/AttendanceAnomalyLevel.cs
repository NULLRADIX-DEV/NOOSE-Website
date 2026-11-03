namespace NOOSE_Website.Models.Enums;

/// <summary>How conspicuous an agent's unexcused-absence record is.</summary>
public enum AttendanceAnomalyLevel
{
    /// <summary>Within the thresholds.</summary>
    None = 0,
    /// <summary>Too few evaluated meetings to judge.</summary>
    Insufficient = 1,
    /// <summary>Reached the yellow threshold.</summary>
    Yellow = 2,
    /// <summary>Reached the red threshold.</summary>
    Red = 3,
}

/// <summary>Display labels and colours.</summary>
public static class AttendanceAnomalyLevelDisplay
{
    public static string Name(AttendanceAnomalyLevel level) => level switch
    {
        AttendanceAnomalyLevel.None => "Unauffällig",
        AttendanceAnomalyLevel.Insufficient => "Zu wenig Daten",
        AttendanceAnomalyLevel.Yellow => "Auffällig",
        AttendanceAnomalyLevel.Red => "Stark auffällig",
        _ => "—",
    };

    /// <summary>Dot colour for the anomaly list.</summary>
    public static string Colour(AttendanceAnomalyLevel level) => level switch
    {
        AttendanceAnomalyLevel.None => "#3FB950",
        AttendanceAnomalyLevel.Insufficient => "#9A9488",
        AttendanceAnomalyLevel.Yellow => "#D29922",
        AttendanceAnomalyLevel.Red => "#F85149",
        _ => "#9A9488",
    };

    public static readonly IReadOnlyList<AttendanceAnomalyLevel> All = new[]
    {
        AttendanceAnomalyLevel.None,
        AttendanceAnomalyLevel.Insufficient,
        AttendanceAnomalyLevel.Yellow,
        AttendanceAnomalyLevel.Red,
    };
}
