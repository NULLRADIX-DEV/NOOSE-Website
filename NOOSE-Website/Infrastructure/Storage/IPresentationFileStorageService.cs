namespace NOOSE_Website.Infrastructure.Storage;

/// <summary>Storage for imported Google Slides decks (PDF), outside wwwroot and under its own base path.</summary>
public interface IPresentationFileStorageService
{
    long MaxBytes { get; }

    /// <summary>Saves the PDF and returns the server-assigned file name.</summary>
    Task<string> SaveAsync(byte[] content, CancellationToken cancellationToken = default);

    Stream OpenRead(string fileNameSaved);

    void Delete(string fileNameSaved);
}
