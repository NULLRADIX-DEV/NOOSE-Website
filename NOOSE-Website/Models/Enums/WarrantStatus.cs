using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Stored stage of a warrant; expiry is never stored, see <see cref="WarrantState"/>.</summary>
public enum WarrantStatus
{
    Requested = 0,
    Signed = 1,
    Rejected = 2,
    Cancelled = 3,
}

/// <summary>Stage of a warrant as a reader sees it, with expiry computed on read.</summary>
public enum WarrantState
{
    Requested = 0,
    Valid = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4,
}

/// <summary>Display labels and chip colours per warrant state.</summary>
public static class WarrantStateDisplay
{
    public static string Name(WarrantState state) => state switch
    {
        WarrantState.Requested => "Beantragt",
        WarrantState.Valid => "Gültig",
        WarrantState.Rejected => "Abgelehnt",
        WarrantState.Expired => "Abgelaufen",
        WarrantState.Cancelled => "Aufgehoben",
        _ => "—",
    };

    public static Color ChipColor(WarrantState state) => state switch
    {
        WarrantState.Requested => Color.Info,
        WarrantState.Valid => Color.Success,
        WarrantState.Rejected => Color.Error,
        WarrantState.Expired => Color.Default,
        WarrantState.Cancelled => Color.Warning,
        _ => Color.Default,
    };

    public static string Icon(WarrantState state) => state switch
    {
        WarrantState.Requested => Icons.Material.Filled.HourglassEmpty,
        WarrantState.Valid => Icons.Material.Filled.Verified,
        WarrantState.Rejected => Icons.Material.Filled.Close,
        WarrantState.Expired => Icons.Material.Filled.EventBusy,
        WarrantState.Cancelled => Icons.Material.Filled.Block,
        _ => Icons.Material.Filled.Approval,
    };

    public static readonly IReadOnlyList<WarrantState> All =
    [
        WarrantState.Requested, WarrantState.Valid, WarrantState.Expired, WarrantState.Rejected, WarrantState.Cancelled,
    ];
}

/// <summary>Display labels of the stored stage, for the change protocol.</summary>
public static class WarrantStatusDisplay
{
    public static string Name(WarrantStatus status) => status switch
    {
        WarrantStatus.Requested => "Beantragt",
        WarrantStatus.Signed => "Unterzeichnet",
        WarrantStatus.Rejected => "Abgelehnt",
        WarrantStatus.Cancelled => "Aufgehoben",
        _ => "—",
    };
}
