namespace NOOSE_Website.Models.Enums;

/// <summary>What happens at a drug route station.</summary>
public enum DrugRouteStationKind
{
    Gathering = 0,
    Processing = 1,
    Storage = 2,
    Handover = 3,
    Sale = 4,
    Other = 5,
}

/// <summary>Display labels.</summary>
public static class DrugRouteStationKindDisplay
{
    public static string Name(DrugRouteStationKind kind) => kind switch
    {
        DrugRouteStationKind.Gathering => "Anbau / Sammeln",
        DrugRouteStationKind.Processing => "Verarbeitung",
        DrugRouteStationKind.Storage => "Lager",
        DrugRouteStationKind.Handover => "Übergabe",
        DrugRouteStationKind.Sale => "Verkauf",
        _ => "Sonstiges",
    };

    public static string Icon(DrugRouteStationKind kind) => kind switch
    {
        DrugRouteStationKind.Gathering => MudBlazor.Icons.Material.Filled.Grass,
        DrugRouteStationKind.Processing => MudBlazor.Icons.Material.Filled.Science,
        DrugRouteStationKind.Storage => MudBlazor.Icons.Material.Filled.Warehouse,
        DrugRouteStationKind.Handover => MudBlazor.Icons.Material.Filled.Handshake,
        DrugRouteStationKind.Sale => MudBlazor.Icons.Material.Filled.Storefront,
        _ => MudBlazor.Icons.Material.Filled.Place,
    };

    public static readonly IReadOnlyList<DrugRouteStationKind> All = Enum.GetValues<DrugRouteStationKind>();
}
