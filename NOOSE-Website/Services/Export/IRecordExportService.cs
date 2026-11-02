using System.Security.Claims;
using NOOSE_Website.Models.Export;

namespace NOOSE_Website.Services.Export;

/// <summary>Carries a record list or a whole record out of the site as a file; every export lands in the access log.</summary>
public interface IRecordExportService
{
    /// <summary>The rows a list shows, as a CSV file.</summary>
    /// <remarks>The caller passes what its grid holds after filter and sort; the rows were loaded through the list's
    /// own visibility predicates, so the file never holds more than the screen.</remarks>
    Task<ExportFile> ListAsync(string recordType, IEnumerable<string> head, IEnumerable<IEnumerable<string>> rows,
        ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>One record's complete dossier as Markdown; null when it does not exist or is hidden from the viewer.</summary>
    Task<ExportFile?> DossierAsync(string recordType, string recordId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default);
}
