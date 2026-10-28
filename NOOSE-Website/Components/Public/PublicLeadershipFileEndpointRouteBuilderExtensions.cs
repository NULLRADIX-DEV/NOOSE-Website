using Microsoft.AspNetCore.Mvc;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Components.Public;

/// <summary>Photo on a leadership card of the public roster — an anonymous file endpoint.</summary>
/// <remarks>
/// It streams a COPY under the public upload path, never an agent's own avatar: that one stays behind
/// <c>Policies.ActiveAgent</c> at <c>/dateien/agenten/profilbild</c>. It lives under <c>/fuehrung</c> because that
/// prefix is already public to the crawler and to <c>PublicIndexingMiddleware</c>.
/// </remarks>
public static class PublicLeadershipFileEndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapNoosePublicLeadershipFileEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/fuehrung");

        group.MapGet("/foto/{fileName}", async (
            string fileName,
            [FromServices] IPublicLeadershipService leadership,
            [FromServices] IPublicLeadershipPhotoStorageService storage,
            CancellationToken cancellationToken) =>
        {
            // one answer for every miss — unknown, no longer on a card, module off, kill switch, missing file
            var photo = await leadership.GetPublishedPhotoAsync(fileName, cancellationToken);
            if (photo is null)
            {
                return Results.NotFound();
            }

            Stream stream;
            try
            {
                stream = storage.OpenRead(photo.FileNameSaved);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or ArgumentException)
            {
                return Results.NotFound();
            }

            // no access log: an anonymous visitor is nobody, and the row would flood the log with actor-less entries
            return Results.File(stream, photo.ContentType, enableRangeProcessing: true);
        })
        .AllowAnonymous();

        return group;
    }
}
