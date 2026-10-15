namespace NOOSE_Website.Models.Enums;

/// <summary>Who may see a presentation: every internal agent, one group, or the invited agents.</summary>
public enum PresentationAccess
{
    Public = 0,
    Closed = 1,
    Private = 2,
}

/// <summary>How one slide is laid out; a PDF page comes from an imported Google Slides file.</summary>
public enum SlideLayout
{
    Title = 0,
    TitleText = 1,
    TextImageLeft = 2,
    TextImageRight = 3,
    Image = 4,
    Section = 5,
    PdfPage = 6,
}

/// <summary>Whether viewers stay on the presenter's slide or may page through on their own.</summary>
public enum PresentationFollowMode
{
    Strict = 0,
    Free = 1,
}

/// <summary>Display labels.</summary>
public static class PresentationDisplay
{
    public static string Name(PresentationAccess access) => access switch
    {
        PresentationAccess.Public => "Öffentlich (alle Agenten)",
        PresentationAccess.Closed => "Geschlossen (eine Gruppe)",
        PresentationAccess.Private => "Privat (nur Eingeladene)",
        _ => "—",
    };

    public static string Icon(PresentationAccess access) => access switch
    {
        PresentationAccess.Public => MudBlazor.Icons.Material.Filled.Public,
        PresentationAccess.Closed => MudBlazor.Icons.Material.Filled.Groups,
        _ => MudBlazor.Icons.Material.Filled.Lock,
    };

    public static string Name(SlideLayout layout) => layout switch
    {
        SlideLayout.Title => "Titelfolie",
        SlideLayout.TitleText => "Titel und Text",
        SlideLayout.TextImageLeft => "Bild links, Text rechts",
        SlideLayout.TextImageRight => "Text links, Bild rechts",
        SlideLayout.Image => "Nur Bild",
        SlideLayout.Section => "Abschnitt",
        SlideLayout.PdfPage => "Google-Folie",
        _ => "—",
    };

    public static string Icon(SlideLayout layout) => layout switch
    {
        SlideLayout.Title => MudBlazor.Icons.Material.Filled.Title,
        SlideLayout.TitleText => MudBlazor.Icons.Material.Filled.Notes,
        SlideLayout.TextImageLeft => MudBlazor.Icons.Material.Filled.VerticalSplit,
        SlideLayout.TextImageRight => MudBlazor.Icons.Material.Filled.VerticalSplit,
        SlideLayout.Image => MudBlazor.Icons.Material.Filled.Image,
        SlideLayout.Section => MudBlazor.Icons.Material.Filled.Bookmark,
        _ => MudBlazor.Icons.Material.Filled.Slideshow,
    };

    public static readonly IReadOnlyList<PresentationAccess> AllAccess =
        [PresentationAccess.Public, PresentationAccess.Closed, PresentationAccess.Private];

    /// <summary>Layouts an author picks by hand; PDF pages only come from an import.</summary>
    public static readonly IReadOnlyList<SlideLayout> EditableLayouts =
        [SlideLayout.Title, SlideLayout.TitleText, SlideLayout.TextImageLeft, SlideLayout.TextImageRight,
         SlideLayout.Image, SlideLayout.Section];

    /// <summary>Audiences a closed presentation may target; "all" is what public means.</summary>
    public static readonly IReadOnlyList<AnnouncementAudience> ClosedAudiences =
        [AnnouncementAudience.TruUnit, AnnouncementAudience.HrbUnit, AnnouncementAudience.Taskforce,
         AnnouncementAudience.FromRank];

    public static bool HasText(SlideLayout layout) => layout is not (SlideLayout.Image or SlideLayout.PdfPage);

    public static bool HasImage(SlideLayout layout)
        => layout is SlideLayout.TextImageLeft or SlideLayout.TextImageRight or SlideLayout.Image;
}
