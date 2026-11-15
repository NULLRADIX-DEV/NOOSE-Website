using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.DrugRoutes;

/// <summary>Protected drug route photo endpoints; classified only for the record's audience.</summary>
public static class DrugRoutesFileEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNooseDrugRoutesFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dateien/drogenrouten");

        group.MapGet("/foto/{photoId}", async (
            string photoId,
            [FromServices] IDrugRouteService routeService,
            [FromServices] IDrugRoutePhotoStorageService storage,
            [FromServices] IAccessLogService access,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var photo = await routeService.GetPhotoWithRouteAsync(photoId, ViewerScope.From(http.User), cancellationToken);
            // not found / not visible to viewer (partner-gated in the service)
            if (photo?.DrugRoute is null)
            {
                return Results.NotFound();
            }

            Stream stream;
            try
            {
                stream = storage.OpenRead(photo.FileNameSaved);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Results.NotFound();
            }

            await access.LogViewAsync(nameof(DrugRoutePhoto), photoId, cancellationToken);

            return Results.File(stream, photo.ContentType, enableRangeProcessing: true);
        })
        .RequireAuthorization(Policies.ActiveAgent);

        return group;
    }
}
