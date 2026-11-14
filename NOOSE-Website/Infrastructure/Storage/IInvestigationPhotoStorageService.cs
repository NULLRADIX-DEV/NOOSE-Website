namespace NOOSE_Website.Infrastructure.Storage;

/// <summary>Evidence pictures of investigations outside wwwroot; own base path, so no other file endpoint reaches them.</summary>
public interface IInvestigationPhotoStorageService
{
    long MaxBytes { get; }

    bool IsAllowedType(string contentType);

    /// <summary>Saves the content and returns the server-assigned file name.</summary>
    Task<string> SaveAsync(Stream content, string contentType, CancellationToken cancellationToken = default);

    /// <summary>Opens a stored file for reading; caller disposes the stream.</summary>
    Stream OpenRead(string fileNameSaved);

    void Delete(string fileNameSaved);
}
