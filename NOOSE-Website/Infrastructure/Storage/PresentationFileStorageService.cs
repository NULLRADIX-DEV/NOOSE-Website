using Microsoft.Extensions.Options;

namespace NOOSE_Website.Infrastructure.Storage;

/// <inheritdoc />
public class PresentationFileStorageService : IPresentationFileStorageService
{
    private readonly FileUploadOptions _options;
    private readonly string _basePath;

    public PresentationFileStorageService(IWebHostEnvironment env, IOptions<FileUploadOptions> options)
    {
        _options = options.Value;
        _basePath = Path.IsPathRooted(_options.PresentationsPath)
            ? _options.PresentationsPath
            : Path.Combine(env.ContentRootPath, _options.PresentationsPath);
    }

    public long MaxBytes => _options.PresentationMaxBytes;

    public async Task<string> SaveAsync(byte[] content, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_basePath);
        // the name is the server's own: nothing of the upload's name reaches the disk
        var fileName = $"{Guid.NewGuid():N}.pdf";
        await File.WriteAllBytesAsync(Path.Combine(_basePath, fileName), content, cancellationToken);
        return fileName;
    }

    public Stream OpenRead(string fileNameSaved)
        => File.OpenRead(FilePathHelper.SafePath(_basePath, fileNameSaved));

    public void Delete(string fileNameSaved)
    {
        var path = FilePathHelper.SafePath(_basePath, fileNameSaved);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
