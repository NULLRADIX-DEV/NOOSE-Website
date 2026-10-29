using System.Security.Claims;
using System.Text;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure.Export;
using NOOSE_Website.Models.Export;

namespace NOOSE_Website.Services.Export;

public sealed class RecordExportService(IDbContextFactory<AppDbContext> dbFactory, IAccessLogService accessLog)
    : IRecordExportService
{
    public async Task<ExportFile> ListAsync(string recordType, IEnumerable<string> head,
        IEnumerable<IEnumerable<string>> rows, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        Permission.RequireListExport(viewer);
        var slug = ExportableRecords.Slug(recordType);

        var list = rows.ToList();
        var bytes = CsvHelper.Generate(head, list);
        await accessLog.LogViewAsync(ExportLog.Type, ExportLog.List(recordType, list.Count), cancellationToken);
        return new ExportFile($"{slug}-{MeetingTime.Day(DateTime.UtcNow):yyyy-MM-dd}.csv", "text/csv; charset=utf-8", bytes);
    }

    public async Task<ExportFile?> DossierAsync(string recordType, string recordId, ClaimsPrincipal viewer,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireDossierExport(viewer);
        _ = ExportableRecords.Slug(recordType); // throws for a type that may not leave

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var scope = ViewerScope.From(viewer);
        if (!await Visibility.IsRecordVisibleAsync(db, recordType, recordId, scope, cancellationToken))
        {
            return null;
        }
        var dossier = await DossierContextBuilder.BuildCompleteAsync(db, recordType, recordId, scope, cancellationToken);
        if (dossier is not { } d)
        {
            return null;
        }

        var markdown = DossierMarkdown.Render(d, viewer.GetCodename() ?? "unbekannt", DateTime.UtcNow);
        await accessLog.LogViewAsync(ExportLog.Type, ExportLog.Dossier(recordType, recordId), cancellationToken);
        return new ExportFile(DossierMarkdown.FileName(d.Title), "text/markdown; charset=utf-8",
            Encoding.UTF8.GetBytes(markdown));
    }
}
