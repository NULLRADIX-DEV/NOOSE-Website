namespace NOOSE_Website.Models.Enums;

/// <summary>An agent's answer to an emergency alarm; persisted as int.</summary>
public enum EmergencyAlarmAnswer
{
    /// <summary>Will report in.</summary>
    Coming = 1,
    /// <summary>Cannot or will not report in.</summary>
    NotComing = 2,
}

/// <summary>Display labels.</summary>
public static class EmergencyAlarmAnswerDisplay
{
    public static string Name(EmergencyAlarmAnswer answer) => answer switch
    {
        EmergencyAlarmAnswer.Coming => "Komme",
        EmergencyAlarmAnswer.NotComing => "Komme nicht",
        _ => "—",
    };

    public static readonly IReadOnlyList<EmergencyAlarmAnswer> All = new[]
    {
        EmergencyAlarmAnswer.Coming,
        EmergencyAlarmAnswer.NotComing,
    };
}
