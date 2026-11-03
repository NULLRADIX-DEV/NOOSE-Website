using System.Security.Claims;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <summary>Uploads a photo for a wanted notice into the person file, where the existing picker then offers it.</summary>
/// <remarks>
/// Its own service only because <c>PersonService</c> already depends on <c>IPublicWantedService</c>. It adds no store:
/// the photo is an ordinary person photo, and copy, snapshot and delete stay in <c>PublicWantedService</c>.
/// </remarks>
public interface IWantedPhotoUploadService
{
    /// <summary>Stores the image as a person photo of the notice's file and returns it as a picker option.</summary>
    Task<PublicWantedPhotoOption> UploadAsync(string wantedId, Stream content, string originalName, string contentType,
        long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
