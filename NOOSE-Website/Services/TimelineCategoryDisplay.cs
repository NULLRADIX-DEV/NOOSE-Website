using MudBlazor;
using NOOSE_Website.Models.Timeline;

namespace NOOSE_Website.Services;

/// <summary>Label, colour and icon per timeline category. Shared by the record timeline, the chronicle feed and the dashboard.</summary>
public static class TimelineCategoryDisplay
{
    /// <summary>German label for a category.</summary>
    public static string Name(TimelineCategory category) => category switch
    {
        TimelineCategory.Asset => "Anlage",
        TimelineCategory.Change => "Änderung",
        TimelineCategory.Deletion => "Löschung",
        TimelineCategory.Restoration => "Wiederherstellung",
        TimelineCategory.Classification => "Einstufung",
        TimelineCategory.Doc => "Dok",
        TimelineCategory.Observation => "Observation",
        TimelineCategory.Photo => "Foto",
        TimelineCategory.Relation => "Beziehung",
        TimelineCategory.Membership => "Mitgliedschaft",
        TimelineCategory.Allocation => "Zuteilung",
        TimelineCategory.Link => "Verknüpfung",
        TimelineCategory.Comment => "Kommentar",
        TimelineCategory.Source => "Quelle",
        TimelineCategory.Followup => "Wiedervorlage",
        TimelineCategory.Activity => "Aktivität",
        TimelineCategory.Agenda => "Tagesordnung",
        TimelineCategory.Attendance => "Anwesenheit",
        TimelineCategory.SignOff => "Abmeldung",
        TimelineCategory.ThreatScore => "Bedrohungs-Score",
        _ => category.ToString(),
    };

    /// <summary>A count with its German noun: "1 Kommentar", "3 Kommentare".</summary>
    public static string Counted(TimelineCategory category, int count)
    {
        var (one, many) = category switch
        {
            TimelineCategory.Asset => ("Anlage", "Anlagen"),
            TimelineCategory.Change => ("Änderung", "Änderungen"),
            TimelineCategory.Deletion => ("Löschung", "Löschungen"),
            TimelineCategory.Restoration => ("Wiederherstellung", "Wiederherstellungen"),
            TimelineCategory.Classification => ("Einstufung", "Einstufungen"),
            TimelineCategory.Doc => ("Dok", "Doks"),
            TimelineCategory.Observation => ("Observation", "Observationen"),
            TimelineCategory.Photo => ("Foto", "Fotos"),
            TimelineCategory.Relation => ("Beziehung", "Beziehungen"),
            TimelineCategory.Membership => ("Mitgliedschaft", "Mitgliedschaften"),
            TimelineCategory.Allocation => ("Zuteilung", "Zuteilungen"),
            TimelineCategory.Link => ("Verknüpfung", "Verknüpfungen"),
            TimelineCategory.Comment => ("Kommentar", "Kommentare"),
            TimelineCategory.Source => ("Quelle", "Quellen"),
            TimelineCategory.Followup => ("Wiedervorlage", "Wiedervorlagen"),
            TimelineCategory.Activity => ("Aktivität", "Aktivitäten"),
            TimelineCategory.Agenda => ("Tagesordnungspunkt", "Tagesordnungspunkte"),
            TimelineCategory.Attendance => ("Anwesenheit", "Anwesenheiten"),
            TimelineCategory.SignOff => ("Abmeldung", "Abmeldungen"),
            TimelineCategory.ThreatScore => ("Score-Änderung", "Score-Änderungen"),
            _ => (Name(category), Name(category)),
        };
        return $"{count} {(count == 1 ? one : many)}";
    }

    /// <summary>Theme colour for a category.</summary>
    public static Color Colour(TimelineCategory category) => category switch
    {
        TimelineCategory.Asset => Color.Success,
        TimelineCategory.Change => Color.Info,
        TimelineCategory.Deletion => Color.Error,
        TimelineCategory.Restoration => Color.Warning,
        TimelineCategory.Classification => Color.Warning,
        TimelineCategory.Doc => Color.Primary,
        TimelineCategory.Observation => Color.Info,
        TimelineCategory.Relation => Color.Tertiary,
        TimelineCategory.Membership => Color.Secondary,
        TimelineCategory.Allocation => Color.Secondary,
        TimelineCategory.Link => Color.Primary,
        TimelineCategory.Followup => Color.Warning,
        TimelineCategory.Activity => Color.Tertiary,
        TimelineCategory.Agenda => Color.Primary,
        TimelineCategory.Attendance => Color.Secondary,
        TimelineCategory.SignOff => Color.Warning,
        TimelineCategory.ThreatScore => Color.Error,
        _ => Color.Default,
    };

    /// <summary>Material icon for a category.</summary>
    public static string Symbol(TimelineCategory category) => category switch
    {
        TimelineCategory.Asset => Icons.Material.Filled.AddCircle,
        TimelineCategory.Change => Icons.Material.Filled.Edit,
        TimelineCategory.Deletion => Icons.Material.Filled.Delete,
        TimelineCategory.Restoration => Icons.Material.Filled.Restore,
        TimelineCategory.Classification => Icons.Material.Filled.Shield,
        TimelineCategory.Doc => Icons.Material.Filled.Description,
        TimelineCategory.Observation => Icons.Material.Filled.Visibility,
        TimelineCategory.Photo => Icons.Material.Filled.Photo,
        TimelineCategory.Relation => Icons.Material.Filled.People,
        TimelineCategory.Membership => Icons.Material.Filled.GroupAdd,
        TimelineCategory.Allocation => Icons.Material.Filled.AssignmentInd,
        TimelineCategory.Link => Icons.Material.Filled.Link,
        TimelineCategory.Comment => Icons.Material.Filled.Comment,
        TimelineCategory.Source => Icons.Material.Filled.AttachFile,
        TimelineCategory.Followup => Icons.Material.Filled.Schedule,
        TimelineCategory.Activity => Icons.Material.Filled.LocalFireDepartment,
        TimelineCategory.Agenda => Icons.Material.Filled.FormatListNumbered,
        TimelineCategory.Attendance => Icons.Material.Filled.HowToReg,
        TimelineCategory.SignOff => Icons.Material.Filled.EventBusy,
        TimelineCategory.ThreatScore => Icons.Material.Filled.Speed,
        _ => Icons.Material.Filled.Circle,
    };

    /// <summary>Hex colour for the density band and other non-MudBlazor surfaces.</summary>
    public static string Hex(TimelineCategory category) => category switch
    {
        TimelineCategory.Asset => "#3FB950",
        TimelineCategory.Change => "#B5A488",
        TimelineCategory.Deletion => "#F85149",
        TimelineCategory.Restoration => "#C98BDB",
        TimelineCategory.Classification => "#D29922",
        TimelineCategory.Doc => "#DB6FB4",
        TimelineCategory.Observation => "#DB6FB4",
        TimelineCategory.Photo => "#DB6FB4",
        TimelineCategory.Relation => "#A371F7",
        TimelineCategory.Membership => "#A3C23B",
        TimelineCategory.Allocation => "#ECE8E1",
        TimelineCategory.Link => "#C98BDB",
        TimelineCategory.Comment => "#9A9488",
        TimelineCategory.Source => "#DB6FB4",
        TimelineCategory.Followup => "#D29922",
        TimelineCategory.Activity => "#F0883E",
        TimelineCategory.ThreatScore => "#F85149",
        _ => "#9A9488",
    };
}
