using System.Security.Claims;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.DrugRoutes;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>Business logic for drug route records: CRUD, classification, stations, people, photos, history.</summary>
public interface IDrugRouteService
{
    Task<List<DrugRoute>> GetListAsync(ViewerScope scope, ArchiveFilter filter = ArchiveFilter.Active,
        CancellationToken cancellationToken = default);
    Task<DrugRoute?> GetDetailAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default);
    Task<List<DrugRoute>> GetTrashAsync(CancellationToken cancellationToken = default);
    Task<List<DrugRoute>> SearchAsync(string? searchText, bool isLeadership, int max = 20, CancellationToken cancellationToken = default);

    /// <summary>Visible routes run by a faction (active and archived, archived last).</summary>
    Task<List<DrugRoute>> GetForFactionAsync(string factionId, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Visible routes a person is involved in.</summary>
    Task<List<DrugRoute>> GetForPersonAsync(string personId, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Throws <see cref="DrugRouteDuplicateException"/> when an active route carries the designation.</summary>
    Task<DrugRoute> CreateAsync(DrugRouteInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task RefreshAsync(string id, DrugRouteInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Moves the record to the trash; the reason is required and lands on its timeline.</summary>
    Task DeleteAsync(string id, string reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Back from the trash; refused while another active route carries the designation.</summary>
    Task RestoreAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task ArchiveAsync(string id, string? reason, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Back into the stock; refused while another active route carries the designation.</summary>
    Task UnarchiveAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    Task ClassificationSetAsync(string id, Classification @new, string? justification, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task<List<ClassificationHistory>> GetClassificationHistoryAsync(string id, ViewerScope scope, CancellationToken cancellationToken = default);

    /// <summary>Stations in route order; partners only see released ones.</summary>
    Task<List<DrugRouteStation>> GetStationsAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default);
    Task<DrugRouteStation> StationAddAsync(string routeId, DrugRouteStationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task StationChangeAsync(string stationId, DrugRouteStationInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Removes the station; its photos stay on the route gallery.</summary>
    Task StationRemoveAsync(string stationId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    /// <summary>Swaps the station with its neighbour (-1 up, +1 down).</summary>
    Task StationMoveAsync(string stationId, int direction, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Involved people incl. person; hidden people filtered by scope.</summary>
    Task<List<DrugRoutePerson>> GetPeopleAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default);
    Task PersonAddAsync(string routeId, DrugRoutePersonInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task PersonChangeAsync(string entryId, string? role, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task PersonRemoveAsync(string entryId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Audit entries of the route and its children (record history; classified-filtered).</summary>
    Task<List<AuditLog>> GetHistoryAsync(string routeId, bool isLeadership, CancellationToken cancellationToken = default);

    /// <summary>Photos of the route (title image first, then by capture time); partners only see released ones.</summary>
    Task<List<DrugRoutePhoto>> GetPhotosAsync(string routeId, ViewerScope scope, CancellationToken cancellationToken = default);
    /// <summary>Loads a photo with its route for the protected endpoint, gated to the viewer; null if not visible.</summary>
    Task<DrugRoutePhoto?> GetPhotoWithRouteAsync(string photoId, ViewerScope scope, CancellationToken cancellationToken = default);
    /// <summary>Adds a photo to the route or one of its stations; the first photo becomes the title image.</summary>
    Task<DrugRoutePhoto> PhotoAddAsync(string routeId, string? stationId, string? caption, Stream content, string originalName,
        string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task PhotoCaptionSetAsync(string photoId, string? caption, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task PhotoRemoveAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
    Task AsTitleImageSetAsync(string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
