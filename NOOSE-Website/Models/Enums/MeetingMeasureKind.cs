using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>What a measure decided in a meeting became: a job with an assignee, or a follow-up on the meeting.</summary>
public enum MeetingMeasureKind
{
    Aufgabe = 0,
    Wiedervorlage = 1,
}

/// <summary>Display labels and icons.</summary>
public static class MeetingMeasureKindDisplay
{
    public static string Name(MeetingMeasureKind kind) => kind switch
    {
        MeetingMeasureKind.Aufgabe => "Aufgabe",
        MeetingMeasureKind.Wiedervorlage => "Wiedervorlage",
        _ => "—",
    };

    public static string Icon(MeetingMeasureKind kind) => kind switch
    {
        MeetingMeasureKind.Aufgabe => Icons.Material.Filled.AddTask,
        MeetingMeasureKind.Wiedervorlage => Icons.Material.Filled.Alarm,
        _ => Icons.Material.Filled.HelpOutline,
    };

    public static readonly IReadOnlyList<MeetingMeasureKind> All = new[]
    {
        MeetingMeasureKind.Aufgabe,
        MeetingMeasureKind.Wiedervorlage,
    };
}
