using MudBlazor;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Navigation;

namespace NOOSE_Website.Services;

/// <summary>What a route points at: its catalog section and, for detail routes, the record identity/name.</summary>
public readonly record struct NavLocation(
    NavEntry? Section,
    string? EntityType,
    string? EntityId,
    string? RecordName,
    string RecordIcon,
    string Route)
{
    /// <summary>True when the route is a known record detail page.</summary>
    public bool IsRecord => EntityType is not null && EntityId is not null;
}

/// <summary>Resolves a relative path to its nav section and (for visible detail routes) the record's display name.</summary>
public interface INavLabelService
{
    /// <summary>Resolve route to a location; record name only when visible to the viewer.</summary>
    Task<NavLocation> ResolveAsync(string? relativePath, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Detail route prefixes and their record type/icon (no DB).</summary>
    static (string TypeKey, string Icon)? RecordTypeForPrefix(string prefix) => prefix switch
    {
        "personen" => (nameof(Person), Icons.Material.Filled.Badge),
        "fraktionen" => (nameof(Faction), Icons.Material.Filled.Groups),
        "personengruppen" => (nameof(PersonGroup), Icons.Material.Filled.Diversity3),
        "parteien" => (nameof(Party), Icons.Material.Filled.AccountBalance),
        "drogenrouten" => (nameof(DrugRoute), Icons.Material.Filled.Route),
        "operationen" => (nameof(Operation), Icons.Material.Filled.Radar),
        "vorgaenge" => (nameof(Case), Icons.Material.Filled.FolderSpecial),
        "dokumente" => (nameof(Document), Icons.Material.Filled.MenuBook),
        "gesetze" => (nameof(Law), Icons.Material.Filled.Gavel),
        "taskforces" => (nameof(Taskforce), Icons.Material.Filled.Groups2),
        _ => null,
    };
}
