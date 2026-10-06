using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Statistics;

namespace NOOSE_Website.Services.Statistics;

/// <summary>Generates, archives and reads the monthly situation reports; each report freezes the full statistics snapshot as JSON. At most one active report per month.</summary>
public interface ISituationReportService
{
    /// <summary>Generates and persists the report for the month; returns null if one exists and replaceExisting is false, otherwise replaces it. triggerId is the manual trigger or null for the service.</summary>
    Task<SituationReport?> GenerateMonthAsync(int year, int month, bool replaceExisting, string? triggerId,
        CancellationToken cancellationToken = default);

    /// <summary>Generates the previous month's report if none exists yet; for the background service. Returns true if a new report was created.</summary>
    Task<bool> GenerateDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default);

    /// <summary>All archived reports as headers (newest first), without the JSON snapshot.</summary>
    Task<List<SituationReportHead>> GetArchiveAsync(CancellationToken cancellationToken = default);

    /// <summary>Loads a report including its deserialized snapshot; null if not found/readable.</summary>
    Task<SituationReportDisplay?> GetDisplayAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>Archive headers for a viewer: leadership and supervision, or a partner with the reports function (no author).</summary>
    Task<List<SituationReportHead>> GetArchiveForAsync(ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>A report for a viewer; partners get the redacted view without financing.</summary>
    Task<SituationReportDisplay?> GetDisplayForAsync(string id, ClaimsPrincipal viewer, CancellationToken cancellationToken = default);

    /// <summary>Moves a report to the trash (soft delete). Leadership only.</summary>
    Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}
