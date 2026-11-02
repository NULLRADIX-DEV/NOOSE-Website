namespace NOOSE_Website.Models.Enums;

/// <summary>Announcement target audience.</summary>
public enum AnnouncementAudience
{
    /// <summary>All active agents.</summary>
    AllActive = 0,
    /// <summary>Specific taskforce members.</summary>
    Taskforce = 1,
    /// <summary>HRT only (stored as the legacy TRU unit).</summary>
    TruUnit = 2,
    /// <summary>Agents from minimum rank.</summary>
    FromRank = 3,
    /// <summary>Administration Division only (stored as the legacy HRB unit).</summary>
    HrbUnit = 4,
}

/// <summary>Display labels.</summary>
public static class AnnouncementAudienceDisplay
{
    public static string Name(AnnouncementAudience audience) => audience switch
    {
        AnnouncementAudience.AllActive => "Alle aktiven Agenten",
        AnnouncementAudience.Taskforce => "Bestimmte Taskforce",
        AnnouncementAudience.TruUnit => "HRT",
        AnnouncementAudience.HrbUnit => "Administration Division",
        AnnouncementAudience.FromRank => "Ab Dienstgrad",
        _ => "—",
    };

    public static readonly IReadOnlyList<AnnouncementAudience> All = new[]
    {
        AnnouncementAudience.AllActive,
        AnnouncementAudience.Taskforce,
        AnnouncementAudience.TruUnit,
        AnnouncementAudience.HrbUnit,
        AnnouncementAudience.FromRank,
    };
}
