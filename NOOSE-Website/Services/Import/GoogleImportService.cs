using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Import;

/// <summary>Whether an import keeps Google's look read-only or becomes an ordinary, editable document.</summary>
public enum GoogleImportMode
{
    /// <summary>1:1 as in Google, text locked, refreshable from the source.</summary>
    Original = 0,

    /// <summary>Simplified into the house editor's formats; from then on an ordinary document.</summary>
    Editable = 1,
}

/// <summary>A Google Docs import: a shared link, a file picked through the Google sign-in or an uploaded zip, plus the
/// library fields.</summary>
public sealed class GoogleDocImportInput
{
    public string? Link { get; set; }
    public GoogleDrivePick? Drive { get; set; }
    public byte[]? UploadZip { get; set; }
    public GoogleImportMode Mode { get; set; }

    /// <summary>Title; empty takes the title of the Google document.</summary>
    public string? Title { get; set; }
    public string? Category { get; set; }
    public DocumentClassification Classification { get; set; }
}

/// <summary>Imports Google Docs into the document library and keeps imported originals current.</summary>
public interface IGoogleImportService
{
    Task<Document> ImportDocumentAsync(GoogleDocImportInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Fetches an imported original again: from a new zip, from the file picked through the Google sign-in,
    /// or, with neither, from the stored link. A picked file must be the one the document came from.</summary>
    Task ReimportDocumentAsync(string documentId, byte[]? uploadZip, GoogleDrivePick? drive, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default);

    /// <summary>Drops the 1:1 original and keeps the simplified copy as an ordinary editable document.</summary>
    Task ConvertToEditableAsync(string documentId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IGoogleImportService" />
/// <remarks>
/// Internal agents with write access only: partners may author documents, but not make the server fetch from
/// outside. The quota (<see cref="GoogleImportQuota"/>, shared with the Slides import) is taken here and not in the
/// rate-limit middleware, because an import started from a Blazor button travels over SignalR and never passes it.
/// Pictures become <see cref="TextImage"/> rows of the document,
/// so they are served under the document's own classification like every other picture in a document.
/// </remarks>
public sealed partial class GoogleImportService(
    IDbContextFactory<AppDbContext> dbFactory,
    IGoogleExportFetcher fetcher,
    ITextImageStorageService imageStorage,
    ILogger<GoogleImportService> logger) : IGoogleImportService
{
    /// <summary>Imports and refreshes one agent may start within the quota window, Docs and Slides together.</summary>
    public const int QuotaPerWindow = GoogleImportQuota.PerWindow;

    public async Task<Document> ImportDocumentAsync(GoogleDocImportInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireInternalAgent(actor);
        Permission.RequireMayAssignClassification(actor, input.Classification);

        var (sourceKind, sourceId, zip, sourceName) = await ResolveSourceAsync(input.Link, input.Drive, input.UploadZip, actor,
            cancellationToken);
        var (html, images) = GoogleDocConverter.ReadZip(zip);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var document = new Document();
        var saved = new List<string>();
        try
        {
            var conversion = await GoogleDocConverter.ConvertAsync(html, images,
                image => StoreImageAsync(db, document.Id, image, saved, cancellationToken));
            RequireContent(conversion);

            document.Title = Cap(TestEngine.Trim(input.Title) ?? conversion.Title ?? sourceName ?? "Google-Dokument", 300);
            document.Category = input.Category.TrimToNull() is { } category ? Cap(category, 120) : null;
            document.Classification = input.Classification;
            document.ContentHtml = conversion.EditableHtml;
            document.ImportedHtml = input.Mode == GoogleImportMode.Original ? conversion.OriginalHtml : null;
            Stamp(document, sourceKind, sourceId, actor);
            db.Documents.Add(document);
            await db.SaveChangesAsync(cancellationToken);
            return document;
        }
        catch
        {
            foreach (var file in saved)
            {
                imageStorage.Delete(file);
            }
            throw;
        }
    }

    public async Task ReimportDocumentAsync(string documentId, byte[]? uploadZip, GoogleDrivePick? drive, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireInternalAgent(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var document = await LoadVisibleAsync(db, documentId, actor, cancellationToken);
        if (document.ImportedHtml is null)
        {
            throw new InvalidOperationException("Nur ein importiertes Original lässt sich aktualisieren.");
        }
        RequireSameFile(drive, document.ImportSourceId);
        var link = document.ImportSourceKind == ImportSourceKind.Link && uploadZip is null && drive is null
            ? $"https://docs.google.com/document/d/{document.ImportSourceId}/"
            : null;
        if (link is null && uploadZip is null && drive is null)
        {
            throw new InvalidOperationException(document.ImportSourceKind == ImportSourceKind.Drive
                ? "Wähle die Datei über die Google-Anmeldung oder lade die aktuelle Fassung als Zip hoch."
                : "Lade die aktuelle Fassung als Zip hoch („Webseite (.html, gezippt)“).");
        }
        var (sourceKind, sourceId, zip, _) = await ResolveSourceAsync(link, drive, uploadZip, actor, cancellationToken);
        sourceKind = KeptKind(document.ImportSourceKind, sourceKind);
        var (html, images) = GoogleDocConverter.ReadZip(zip);
        var before = await ImageIdsAsync(db, document.Id, cancellationToken);

        var saved = new List<string>();
        try
        {
            var conversion = await GoogleDocConverter.ConvertAsync(html, images,
                image => StoreImageAsync(db, document.Id, image, saved, cancellationToken));
            RequireContent(conversion);
            document.ContentHtml = conversion.EditableHtml;
            document.ImportedHtml = conversion.OriginalHtml;
            Stamp(document, sourceKind, sourceId, actor);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            foreach (var file in saved)
            {
                imageStorage.Delete(file);
            }
            throw;
        }

        await SweepImagesAsync(document.Id, before, document.ImportedHtml + document.ContentHtml, cancellationToken);
    }

    public async Task ConvertToEditableAsync(string documentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireInternalAgent(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var document = await LoadVisibleAsync(db, documentId, actor, cancellationToken);
        if (document.ImportedHtml is null)
        {
            return;
        }
        var before = await ImageIdsAsync(db, document.Id, cancellationToken);
        document.ImportedHtml = null;
        await db.SaveChangesAsync(cancellationToken);

        // a picture the simplified copy dropped is no longer shown anywhere
        await SweepImagesAsync(document.Id, before, document.ContentHtml, cancellationToken);
    }

    /// <summary>The export's bytes, where they came from and, through the sign-in, the file's name in Drive; a link
    /// wins over a picked file, a picked file over an upload. The quota is taken only once the source is valid, so a
    /// mistyped link costs none.</summary>
    private async Task<(ImportSourceKind Kind, string? Id, byte[] Zip, string? Name)> ResolveSourceAsync(string? link,
        GoogleDrivePick? drive, byte[]? upload, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(link))
        {
            var fileId = GoogleLinks.FileId(link, GoogleFileKind.Document)
                ?? throw new GoogleExportException(
                    "Das ist kein Link auf ein Google-Dokument (https://docs.google.com/document/d/…).");
            GoogleImportQuota.Take(actor);
            return (ImportSourceKind.Link, fileId, await fetcher.FetchDocumentZipAsync(fileId, cancellationToken), null);
        }
        if (drive is not null)
        {
            GoogleExportFetcher.RequireUsable(drive, fetcher.IsDriveConfigured);
            GoogleImportQuota.Take(actor);
            var export = await fetcher.FetchDriveDocumentZipAsync(drive, cancellationToken);
            return (ImportSourceKind.Drive, drive.FileId, export.Bytes, export.Name);
        }
        if (upload is { Length: > 0 })
        {
            if (upload.Length > GoogleExportFetcher.MaxBytes)
            {
                throw new GoogleExportException("Die Datei ist zu groß (höchstens 25 MB).");
            }
            GoogleImportQuota.Take(actor);
            return (ImportSourceKind.Upload, null, upload, null);
        }
        throw new InvalidOperationException("Gib einen Freigabe-Link an, wähle die Datei über Google oder lade sie hoch.");
    }

    /// <summary>The same Google file read through the sign-in leaves a link import a link import: everyone can still
    /// refresh it by link, without a Google account of their own.</summary>
    internal static ImportSourceKind KeptKind(ImportSourceKind? before, ImportSourceKind now)
        => before == ImportSourceKind.Link && now == ImportSourceKind.Drive ? ImportSourceKind.Link : now;

    /// <summary>A refresh through the sign-in reads the file the import came from, not another one.</summary>
    internal static void RequireSameFile(GoogleDrivePick? drive, string? sourceId)
    {
        if (drive is not null && sourceId is not null && !string.Equals(drive.FileId, sourceId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Das ist eine andere Google-Datei als die, aus der dieser Import stammt.");
        }
    }

    private static async Task<Document> LoadVisibleAsync(AppDbContext db, string documentId, ClaimsPrincipal actor,
        CancellationToken cancellationToken)
        => await db.Documents.OnlyVisible(db, DocumentViewerScope.From(actor))
            .FirstOrDefaultAsync(d => d.Id == documentId, cancellationToken)
            ?? throw new InvalidOperationException("Dokument nicht gefunden.");

    private static void RequireContent(GoogleDocConversion conversion)
    {
        if (HtmlCleanup.PlainText(conversion.EditableHtml).Length == 0 && !conversion.EditableHtml.Contains("<img", StringComparison.Ordinal))
        {
            throw new GoogleExportException("Das Dokument ist leer.");
        }
    }

    private static void Stamp(Document document, ImportSourceKind kind, string? sourceId, ClaimsPrincipal actor)
    {
        document.ImportSourceKind = kind;
        document.ImportSourceId = sourceId;
        document.ImportedAt = DateTime.UtcNow;
        document.ImportedById = actor.GetAgentId();
    }

    /// <summary>Files one picture under the document and returns the URL its img tag gets; null drops it.</summary>
    private async Task<string?> StoreImageAsync(AppDbContext db, string documentId, ImportedImage image,
        List<string> saved, CancellationToken cancellationToken)
    {
        if (!imageStorage.IsAllowedType(image.ContentType) || image.Bytes.Length == 0
            || image.Bytes.Length > imageStorage.MaxBytes)
        {
            return null;
        }
        using var content = new MemoryStream(image.Bytes);
        var fileName = await imageStorage.SaveAsync(content, image.ContentType, cancellationToken);
        saved.Add(fileName);
        var row = new TextImage
        {
            EntityType = nameof(Document),
            EntityId = documentId,
            FileNameSaved = fileName,
            ContentType = image.ContentType,
            SizeBytes = image.Bytes.Length,
        };
        db.TextImages.Add(row);
        return $"/dateien/textbilder/{row.Id}";
    }

    private static Task<List<string>> ImageIdsAsync(AppDbContext db, string documentId, CancellationToken cancellationToken)
        => db.TextImages.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.EntityType == nameof(Document) && t.EntityId == documentId)
            .Select(t => t.Id).ToListAsync(cancellationToken);

    /// <summary>Removes the document's pictures that were there before and are referenced by neither text now.</summary>
    /// <remarks>After the commit and best effort, like the interceptor's own sweep: leftover bytes are housekeeping,
    /// a failed refresh is not. The interceptor only sees ContentHtml, so pictures that lived in the original alone
    /// would otherwise stay forever.</remarks>
    private async Task SweepImagesAsync(string documentId, IReadOnlyCollection<string> before, string? html,
        CancellationToken cancellationToken)
    {
        try
        {
            var kept = StoredImage().Matches(html ?? string.Empty).Select(m => m.Groups["id"].Value)
                .ToHashSet(StringComparer.Ordinal);
            var gone = before.Where(id => !kept.Contains(id)).ToList();
            if (gone.Count == 0)
            {
                return;
            }
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.TextImages.IgnoreQueryFilters().AsNoTracking()
                .Where(t => t.EntityType == nameof(Document) && t.EntityId == documentId && gone.Contains(t.Id))
                .Select(t => new { t.Id, t.FileNameSaved }).ToListAsync(cancellationToken);
            var ids = rows.Select(r => r.Id).ToList();
            await db.TextImages.IgnoreQueryFilters().Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(cancellationToken);
            foreach (var row in rows)
            {
                imageStorage.Delete(row.FileNameSaved);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Image sweep after Google import of {DocumentId} failed.", documentId);
        }
    }

    private static string Cap(string value, int length) => value.Length > length ? value[..length].TrimEnd() : value;

    [GeneratedRegex("/dateien/textbilder/(?<id>[0-9a-fA-F-]{36})")]
    private static partial Regex StoredImage();
}
