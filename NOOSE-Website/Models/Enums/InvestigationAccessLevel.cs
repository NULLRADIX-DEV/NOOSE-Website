namespace NOOSE_Website.Models.Enums;

/// <summary>Access to one tab of an investigation; higher wins when several grants meet.</summary>
public enum InvestigationAccessLevel
{
    None = 0,
    Read = 1,
    Edit = 2,
}

/// <summary>Display labels.</summary>
public static class InvestigationAccessLevelDisplay
{
    public static string Name(InvestigationAccessLevel level) => level switch
    {
        InvestigationAccessLevel.Read => "Lesen",
        InvestigationAccessLevel.Edit => "Bearbeiten",
        _ => "Keine",
    };

    public static readonly IReadOnlyList<InvestigationAccessLevel> All = new[]
    {
        InvestigationAccessLevel.None,
        InvestigationAccessLevel.Read,
        InvestigationAccessLevel.Edit,
    };
}
