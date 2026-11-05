using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Operations;

/// <summary>Display names of an emergency alarm's target and answers.</summary>
public static class EmergencyAlarmDisplay
{
    /// <summary>Short name of the target; null = all agents.</summary>
    public static string Target(Division? target) => target is { } d ? DivisionDisplay.Short(d) : "Alle Agenten";

    public static string Answer(EmergencyAlarmAnswer? answer) => answer switch
    {
        EmergencyAlarmAnswer.Coming => "Komme",
        EmergencyAlarmAnswer.NotComing => "Komme nicht",
        _ => "Offen",
    };
}
