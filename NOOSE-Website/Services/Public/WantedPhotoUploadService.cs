using System.Security.Claims;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IWantedPhotoUploadService" />
public class WantedPhotoUploadService(IPublicWantedService wanted, IPersonService people) : IWantedPhotoUploadService
{
    public async Task<PublicWantedPhotoOption> UploadAsync(string wantedId, Stream content, string originalName,
        string contentType, long size, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // the notice's guards first, then the person-photo rules (type, size, classified, audit) unchanged
        var personId = await wanted.RequirePhotoUploadAsync(wantedId, actor, cancellationToken);
        var photo = await people.PhotoAddAsync(personId, content, originalName, contentType, size, actor, cancellationToken);
        return new PublicWantedPhotoOption(photo.Id, photo.CreatedAt.ToString("dd.MM.yyyy"));
    }
}
