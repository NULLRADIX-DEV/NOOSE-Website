using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Investigations;

/// <summary>Evidence pictures of investigations; partners read them too, the investigation's rights decide per piece.</summary>
public static class InvestigationFileEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNooseInvestigationFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dateien/ermittlungen");

        group.MapGet("/foto/{photoId}", async (
            string photoId,
            [FromServices] IInvestigationService investigations,
            [FromServices] IInvestigationPhotoStorageService storage,
            [FromServices] IAccessLogService access,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var photo = await investigations.GetEvidencePhotoAsync(photoId, http.User, cancellationToken);
            if (photo is null)
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

            await access.LogViewAsync(nameof(InvestigationEvidencePhoto), photoId, cancellationToken);
            return Results.File(stream, photo.ContentType, enableRangeProcessing: true);
        })
        .RequireAuthorization(Policies.ActiveAgent);

        return group;
    }
}
