using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using NOOSE_Website.Authorization;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services;

namespace NOOSE_Website.Components.Common;

/// <summary>Delivery of imported Google Slides decks (PDF) to the pdf.js renderer of the presentation pages.</summary>
/// <remarks>
/// Internal agents only; the presentation's own visibility decides, like for its slides, so a closed or private deck
/// reaches no one outside its audience. Ranges are served because pdf.js reads a large deck piece by piece.
/// </remarks>
public static class PresentationFileEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNoosePresentationFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/dateien/praesentationen");

        group.MapGet("/{fileId}", async (
            string fileId,
            [FromQuery] int? r,
            [FromServices] IPresentationService presentations,
            [FromServices] IPresentationFileStorageService storage,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            // one 404 for "gone" and "not yours", like every other file endpoint
            var access = await presentations.GetPdfAccessAsync(fileId, http.User, cancellationToken);
            // a page still reading the deck it loaded before a refresh must not get pieces of the new file
            if (access is null || (r is { } revision && revision != access.Revision))
            {
                return Results.NotFound();
            }

            Stream stream;
            try
            {
                stream = storage.OpenRead(access.FileNameSaved);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Results.NotFound();
            }

            // the bytes were checked as a PDF on import; no browser may read them as anything else
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(stream, "application/pdf",
                entityTag: new EntityTagHeaderValue($"\"{fileId}-{access.Revision}\""), enableRangeProcessing: true);
        })
        .RequireAuthorization(Policies.ActiveAgent, Policies.InternalAgent);

        return group;
    }
}
