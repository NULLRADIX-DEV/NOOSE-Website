using Microsoft.Extensions.Options;

namespace NOOSE_Website.Infrastructure.Storage;

/// <inheritdoc />
/// <remarks>A copy of the wanted-photo store with its own base path: the two must never share a folder.</remarks>
public class PublicLeadershipPhotoStorageService : IPublicLeadershipPhotoStorageService
{
    private readonly FileUploadOptions _options;
    private readonly string _basePath;

    public PublicLeadershipPhotoStorageService(IWebHostEnvironment env, IOptions<FileUploadOptions> options)
    {
        _options = options.Value;
        _basePath = Path.IsPathRooted(_options.LeadershipPath)
            ? _options.LeadershipPath
            : Path.Combine(env.ContentRootPath, _options.LeadershipPath);
    }

    // portrait for an 80px card, served to every visitor; the avatar cap fits that better than the file cap
    public long MaxBytes => _options.AvatarMaxBytes;

    public bool IsAllowedType(string contentType)
        => _options.AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase);

    public async Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_basePath);
        var fileName = $"{Guid.NewGuid():N}{ExtensionFor(contentType)}";
        var target = Path.Combine(_basePath, fileName);

        try
        {
            await using var fs = File.Create(target);
            await content.CopyToAsync(fs, cancellationToken);
        }
        catch
        {
            // a broken upload must not leave half a photo in the public folder
            try { File.Delete(target); }
            catch { /* best effort */ }
            throw;
        }
        return fileName;
    }

    public Stream OpenRead(string fileNameSaved)
        => File.OpenRead(SafePath(fileNameSaved));

    public void Delete(string fileNameSaved)
    {
        var path = SafePath(fileNameSaved);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string SafePath(string fileName) => FilePathHelper.SafePath(_basePath, fileName);

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => ".bin",
    };
}
