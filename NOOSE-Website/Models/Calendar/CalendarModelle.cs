namespace NOOSE_Website.Models.Calendar;

/// <summary>Calendar entry source; controls colour and legend.</summary>
public enum CalendarSource
{
    Appointment = 0,
    Operation = 1,
    Observation = 2,
    Job = 3,
    Followup = 4,
    FactionActivity = 5,
    PersonDoc = 6,
    Meeting = 7,
    Absence = 8,
    TeamAbsence = 9,
}

/// <summary>Requested calendar view.</summary>
public enum CalendarMode
{
    /// <summary>Personal agenda.</summary>
    My = 0,
    /// <summary>Agency-wide view.</summary>
    Authority = 1,
}

/// <summary>Single calendar entry passed to FullCalendar.</summary>
/// <param name="EntityType">Record this entry belongs to, as its CLR type name; null when there is none to name.
/// Only set where the producer already decided the viewer may know the record — a followup on a classified parent
/// carries no reference, exactly as it carries no title and no link.</param>
public record CalendarEntry(
    string Id,
    string Title,
    DateTime StartLocal,
    DateTime? EndLocal,
    bool WholeDay,
    CalendarSource Source,
    string? Href,
    bool Obsolete = false,
    string? EntityType = null,
    string? EntityId = null);

/// <summary>Display helper; colour per source.</summary>
public static class CalendarDisplay
{
    public static string Colour(CalendarSource source) => source switch
    {
        CalendarSource.Appointment => "#3FB950",
        CalendarSource.Operation => "#F0883E",
        CalendarSource.Observation => "#B27923",
        CalendarSource.Job => "#9A9488",
        CalendarSource.Followup => "#D29922",
        CalendarSource.FactionActivity => "#83A412",
        CalendarSource.PersonDoc => "#A371F7",
        CalendarSource.Meeting => "#8A7650",
        CalendarSource.Absence => "#DB61A2",
        // muted sibling of the own-absence pink
        CalendarSource.TeamAbsence => "#B07C9E",
        _ => "#9A9488",
    };

    public static string Name(CalendarSource source) => source switch
    {
        CalendarSource.Appointment => "Termine",
        CalendarSource.Operation => "Operationen",
        CalendarSource.Observation => "Observationen",
        CalendarSource.Job => "Aufgaben (fällig)",
        CalendarSource.Followup => "Wiedervorlagen",
        CalendarSource.FactionActivity => "Fraktions-Aktivitäten",
        CalendarSource.PersonDoc => "Personen-Doks",
        CalendarSource.Meeting => "Besprechungen",
        CalendarSource.Absence => "Meine Abmeldungen",
        CalendarSource.TeamAbsence => "Team-Abmeldungen",
        _ => "—",
    };

    public static readonly IReadOnlyList<CalendarSource> All = new[]
    {
        CalendarSource.Appointment,
        CalendarSource.Operation,
        CalendarSource.Observation,
        CalendarSource.Job,
        CalendarSource.Followup,
        CalendarSource.FactionActivity,
        CalendarSource.PersonDoc,
        CalendarSource.Meeting,
        CalendarSource.Absence,
        CalendarSource.TeamAbsence,
    };
}
