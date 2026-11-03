using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Kind of a warrant: what the signed paper allows.</summary>
public enum WarrantType
{
    Search = 0,
    Seizure = 1,
    Arrest = 2,
}

/// <summary>Display labels and icons per warrant kind.</summary>
public static class WarrantTypeDisplay
{
    public static string Name(WarrantType type) => type switch
    {
        WarrantType.Search => "Durchsuchungsanordnung",
        WarrantType.Seizure => "Beschlagnahmeanordnung",
        WarrantType.Arrest => "Festnahmeanordnung",
        _ => "—",
    };

    /// <summary>What the paper orders, as the line under its heading.</summary>
    public static string Order(WarrantType type) => type switch
    {
        WarrantType.Search => "Die Durchsuchung wird angeordnet.",
        WarrantType.Seizure => "Die Beschlagnahme wird angeordnet.",
        WarrantType.Arrest => "Die Festnahme wird angeordnet.",
        _ => string.Empty,
    };

    public static string Icon(WarrantType type) => type switch
    {
        WarrantType.Search => Icons.Material.Filled.ManageSearch,
        WarrantType.Seizure => Icons.Material.Filled.Inventory2,
        WarrantType.Arrest => Icons.Material.Filled.Gavel,
        _ => Icons.Material.Filled.Approval,
    };

    public static readonly IReadOnlyList<WarrantType> All = [WarrantType.Search, WarrantType.Seizure, WarrantType.Arrest];
}
