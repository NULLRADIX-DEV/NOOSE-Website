using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Models.Announcements;

/// <summary>Form model for creating/editing an announcement; broadcast fields are leadership-only and evaluated only on create.</summary>
public class AnnouncementInput
{
    public string Title { get; set; } = string.Empty;
    public string? Content { get; set; }
    public bool Important { get; set; }

    public AnnouncementAudience Audience { get; set; } = AnnouncementAudience.AllActive;
    /// <summary>Taskforce id when audience targets a taskforce.</summary>
    public string? TargetId { get; set; }
    /// <summary>Minimum rank when audience targets a rank floor.</summary>
    public Rank? MinRank { get; set; }

    public bool AsBroadcast { get; set; }
    public bool AcknowledgmentRequired { get; set; }

    /// <summary>Days until whoever has not acknowledged is reminded; 0 = never. Only read with an acknowledgment.</summary>
    public int AcknowledgmentDays { get; set; } = AcknowledgmentRules.DefaultDays;
}
