using Microsoft.AspNetCore.Mvc;
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
            [FromServices] IPresentationService presentations,
            [FromServices] IPresentationFileStorageService storage,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            // one 404 for "gone" and "not yours", like every other file endpoint
            var fileName = await presentations.GetPdfFileNameAsync(fileId, http.User, cancellationToken);
            if (fileName is null)
            {
                return Results.NotFound();
            }

            Stream stream;
            try
            {
                stream = storage.OpenRead(fileName);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return Results.NotFound();
            }

            // the bytes were checked as a PDF on import; no browser may read them as anything else
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(stream, "application/pdf", enableRangeProcessing: true);
        })
        .RequireAuthorization(Policies.ActiveAgent, Policies.InternalAgent);

        return group;
    }
}
