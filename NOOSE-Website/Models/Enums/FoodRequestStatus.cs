using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Stage of a food request: requested, decided, and finally issued from the evidence room.</summary>
public enum FoodRequestStatus
{
    Requested = 0,
    Approved = 1,
    Rejected = 2,
    Issued = 3,
    Withdrawn = 4,
}

/// <summary>Display labels, icons and chip colours per food-request stage.</summary>
public static class FoodRequestStatusDisplay
{
    public static string Name(FoodRequestStatus status) => status switch
    {
        FoodRequestStatus.Requested => "Angefordert",
        FoodRequestStatus.Approved => "Angenommen",
        FoodRequestStatus.Rejected => "Abgelehnt",
        FoodRequestStatus.Issued => "Ausgegeben",
        FoodRequestStatus.Withdrawn => "Zurückgezogen",
        _ => "—",
    };

    public static string Icon(FoodRequestStatus status) => status switch
    {
        FoodRequestStatus.Requested => Icons.Material.Filled.HourglassEmpty,
        FoodRequestStatus.Approved => Icons.Material.Filled.Check,
        FoodRequestStatus.Rejected => Icons.Material.Filled.Close,
        FoodRequestStatus.Issued => Icons.Material.Filled.Restaurant,
        FoodRequestStatus.Withdrawn => Icons.Material.Filled.Undo,
        _ => Icons.Material.Filled.Fastfood,
    };

    public static Color ChipColor(FoodRequestStatus status) => status switch
    {
        FoodRequestStatus.Requested => Color.Info,
        FoodRequestStatus.Approved => Color.Warning,
        FoodRequestStatus.Rejected => Color.Error,
        FoodRequestStatus.Issued => Color.Success,
        FoodRequestStatus.Withdrawn => Color.Default,
        _ => Color.Default,
    };

    /// <summary>Awaiting a decision, so it shows up in the leadership inbox and the nav badge.</summary>
    public static bool IsOpen(FoodRequestStatus status) => status == FoodRequestStatus.Requested;

    /// <summary>Counts against the weekly limit; approval reserves it, the issue keeps it.</summary>
    public static bool ConsumesLimit(FoodRequestStatus status)
        => status is FoodRequestStatus.Approved or FoodRequestStatus.Issued;

    /// <summary>No further transition is possible.</summary>
    public static bool IsTerminal(FoodRequestStatus status) => status == FoodRequestStatus.Withdrawn;

    public static readonly IReadOnlyList<FoodRequestStatus> All = new[]
    {
        FoodRequestStatus.Requested,
        FoodRequestStatus.Approved,
        FoodRequestStatus.Rejected,
        FoodRequestStatus.Issued,
        FoodRequestStatus.Withdrawn,
    };
}
