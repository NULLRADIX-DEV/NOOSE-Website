using NOOSE_Website.Models.Timeline;

namespace NOOSE_Website.Services;

/// <summary>Category groups, labels and colours of the chronicle's activity band.</summary>
/// <remarks>
/// The band folds the 20 timeline categories into five groups plus a remainder because
/// <see cref="TimelineCategoryDisplay.Hex"/> cannot carry a stack: it yields only ten distinct values
/// for twenty categories (Doc/Observation/Photo/Source all share one), and three of those are theme
/// colours — Allocation is TextPrimary (a near-white segment reads as the total), Comment is DrawerIcon
/// (reads as "no data") and Change is the accent an admin may override at runtime, which would repaint
/// the band. The hexes below are ChartPalette slots (bronze, green, violet, amber, red, muted gray), ordered so
/// every stacked neighbour clears the normal-vision floor on the dark surface #242424.
/// </remarks>
public static class ActivityBandDisplay
{
    /// <summary>Number of stack groups including the remainder; slots are 0..Slots-1.</summary>
    public const int Slots = 6;

    /// <summary>Slot of the remainder group; also the fallback for an unmapped category.</summary>
    public const int OtherSlot = 5;

    // base to crown: routine edits at the bottom, escalations on top
    private static readonly TimelineCategory[][] Groups =
    [
        [TimelineCategory.Change, TimelineCategory.Comment],
        [
            TimelineCategory.Asset, TimelineCategory.Doc, TimelineCategory.Photo,
            TimelineCategory.Source, TimelineCategory.Observation,
        ],
        [
            TimelineCategory.Membership, TimelineCategory.Allocation, TimelineCategory.Relation,
            TimelineCategory.Link, TimelineCategory.Attendance, TimelineCategory.Agenda,
            TimelineCategory.Activity, TimelineCategory.Followup, TimelineCategory.SignOff,
        ],
        [TimelineCategory.Classification, TimelineCategory.Restoration],
        [TimelineCategory.Deletion, TimelineCategory.ThreatScore],
    ];

    private static readonly string[] Colours =
    [
        "#b27923", "#54aa71", "#9a59cf", "#ad941d", "#d3434c", "#7a756c",
    ];

    private static readonly string[] Labels =
    [
        "Bearbeitung", "Inhalte", "Zuordnung", "Einstufung", "Gefährdung", "Übrige",
    ];

    private static readonly Dictionary<TimelineCategory, int> SlotByCategory = Build();

    private static Dictionary<TimelineCategory, int> Build()
    {
        var map = new Dictionary<TimelineCategory, int>();
        for (var slot = 0; slot < Groups.Length; slot++)
        {
            foreach (var category in Groups[slot])
            {
                map[category] = slot;
            }
        }
        return map;
    }

    /// <summary>Stack slot of a category; the remainder slot for anything unmapped.</summary>
    public static int Slot(TimelineCategory category) => SlotByCategory.GetValueOrDefault(category, OtherSlot);

    /// <summary>Categories a slot stands for; empty for the remainder slot.</summary>
    public static IReadOnlyList<TimelineCategory> Categories(int slot)
        => slot >= 0 && slot < Groups.Length ? Groups[slot] : Array.Empty<TimelineCategory>();

    /// <summary>Series colour of a slot; never a theme accent, so an admin override cannot repaint the band.</summary>
    public static string Hex(int slot) => slot >= 0 && slot < Colours.Length ? Colours[slot] : Colours[OtherSlot];

    /// <summary>German legend label of a slot.</summary>
    public static string Label(int slot) => slot >= 0 && slot < Labels.Length ? Labels[slot] : Labels[OtherSlot];
}
