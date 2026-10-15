using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Presentations;

namespace NOOSE_Website.Services.Import;

/// <summary>A Google Slides import: a shared link, a deck picked through the Google sign-in or an uploaded PDF, into a
/// new presentation or an existing one.</summary>
public sealed class SlidesImportInput
{
    public string? Link { get; set; }
    public GoogleDrivePick? Drive { get; set; }
    public byte[]? UploadPdf { get; set; }

    /// <summary>Name of the uploaded file; the deck's title when the PDF carries none.</summary>
    public string? UploadName { get; set; }

    /// <summary>Presentation the pages are appended to; null creates one from <see cref="Settings"/>.</summary>
    public string? PresentationId { get; set; }

    /// <summary>Title and visibility of a new presentation; an empty title takes the deck's own.</summary>
    public PresentationInput? Settings { get; set; }
}

/// <summary>What a refresh changed: the deck's pages now, slides added for new pages, slides removed for lost ones.</summary>
public sealed record SlidesRefreshResult(int Pages, int Added, int Removed);

/// <summary>Imports Google Slides decks as PDF, one slide per page, and keeps them current.</summary>
public interface ISlidesImportService
{
    /// <summary>Imports a deck; returns the id of the presentation that holds its slides.</summary>
    Task<string> ImportAsync(SlidesImportInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Fetches an imported deck again and swaps its pages in place: from a new PDF, from the deck picked
    /// through the Google sign-in (the one the import came from), or, with neither, from the stored link. Slides keep
    /// their place and notes by page number.</summary>
    Task<SlidesRefreshResult> RefreshAsync(string fileId, byte[]? uploadPdf, string? uploadName, GoogleDrivePick? drive,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default);

    /// <summary>Removes an imported deck and every slide that shows one of its pages.</summary>
    Task RemoveAsync(string fileId, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ISlidesImportService" />
/// <remarks>
/// The same gate as creating a presentation, and the owner or leadership for an existing one; the quota is the one
/// the Docs import draws from. The server only checks the PDF and counts its pages - in a child process, see
/// <see cref="IsolatedPdfInspector"/> - and the browser draws them with pdf.js, so the slides stay sharp on a projector
/// and the container needs no native renderer. <see cref="PresentationFile.Revision"/> is a concurrency token: of two
/// refreshes or a refresh and a removal at once, the second fails instead of doubling slides or orphaning files.
/// </remarks>
public sealed class SlidesImportService(
    IDbContextFactory<AppDbContext> dbFactory,
    IGoogleExportFetcher fetcher,
    IPresentationFileStorageService storage,
    IPdfInspector inspector,
    PresentationLiveRegistry live,
    PresentationBroadcaster broadcaster,
    ILogger<SlidesImportService> logger) : ISlidesImportService
{
    private const string DefaultTitle = "Google-Präsentation";

    private const string ChangedMeanwhile =
        "Dieser Import wurde gerade von jemand anderem geändert. Lade die Seite neu und versuche es noch einmal.";

    public async Task<string> ImportAsync(SlidesImportInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // everything the author can get wrong is checked before Google is asked, so a refusal costs no quota
        var isNew = string.IsNullOrEmpty(input.PresentationId);
        var settings = input.Settings ?? new PresentationInput();
        var ownTitle = TestEngine.Trim(settings.Title);
        Presentation presentation;
        var existing = 0;
        if (isNew)
        {
            presentation = new Presentation { OwnerId = actor.GetAgentId() ?? string.Empty };
            // a stand-in passes the title check; the deck's own title replaces it once the PDF is read. A copy, so the
            // dialog's own fields stay as typed when the import fails
            var checkedSettings = new PresentationInput
            {
                Title = ownTitle ?? DefaultTitle,
                Description = settings.Description,
                Access = settings.Access,
                Audience = settings.Audience,
                TargetId = settings.TargetId,
                MinRank = settings.MinRank,
            };
            await PresentationService.ApplyAsync(db, presentation, checkedSettings, actor, cancellationToken);
        }
        else
        {
            presentation = await PresentationService.LoadManagedAsync(db, input.PresentationId!, actor, cancellationToken);
            existing = await db.PresentationSlides.CountAsync(s => s.PresentationId == presentation.Id, cancellationToken);
            RequireRoom(existing, 1);
        }

        var (kind, sourceId, pdf, info, sourceName) = await ResolveSourceAsync(input.Link, input.Drive, input.UploadPdf, actor,
            cancellationToken);
        RequireRoom(existing, info.PageCount);
        var deckTitle = Cap(sourceName ?? info.Title ?? BaseName(input.UploadName) ?? DefaultTitle, 300);
        if (isNew && ownTitle is null)
        {
            presentation.Title = Cap(deckTitle, 200);
        }

        var fileName = await storage.SaveAsync(pdf, cancellationToken);
        try
        {
            var file = new PresentationFile
            {
                PresentationId = presentation.Id,
                FileNameSaved = fileName,
                Title = deckTitle,
                SizeBytes = pdf.Length,
                PageCount = info.PageCount,
            };
            Stamp(file, kind, sourceId, actor);
            var last = isNew
                ? 0
                : await db.PresentationSlides.Where(s => s.PresentationId == presentation.Id)
                    .MaxAsync(s => (int?)s.Sorting, cancellationToken) ?? 0;
            if (isNew)
            {
                // an imported deck brings its own title slide, so none is added
                db.Presentations.Add(presentation);
                await PresentationService.SyncInvitesAsync(db, presentation, settings, cancellationToken);
            }
            else
            {
                presentation.ModifiedAt = DateTime.UtcNow;
            }
            db.PresentationFiles.Add(file);
            db.PresentationSlides.AddRange(Enumerable.Range(1, info.PageCount).Select(page => PageSlide(file, page, last + page)));
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            storage.Delete(fileName);
            throw;
        }
        if (!isNew)
        {
            ReportSlides(presentation.Id);
        }
        return presentation.Id;
    }

    public async Task<SlidesRefreshResult> RefreshAsync(string fileId, byte[]? uploadPdf, string? uploadName, GoogleDrivePick? drive,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (file, presentation) = await LoadManagedFileAsync(db, fileId, actor, cancellationToken);
        GoogleImportService.RequireSameFile(drive, file.SourceId);
        var link = file.SourceKind == ImportSourceKind.Link && uploadPdf is null && drive is null
            ? $"https://docs.google.com/presentation/d/{file.SourceId}/"
            : null;
        if (link is null && uploadPdf is null && drive is null)
        {
            throw new InvalidOperationException(file.SourceKind == ImportSourceKind.Drive
                ? "Wähle die Präsentation über die Google-Anmeldung oder lade die aktuelle Fassung als PDF hoch."
                : "Lade die aktuelle Fassung als PDF hoch („Datei → Herunterladen → PDF-Dokument“).");
        }
        var (kind, sourceId, pdf, info, sourceName) = await ResolveSourceAsync(link, drive, uploadPdf, actor, cancellationToken);
        kind = GoogleImportService.KeptKind(file.SourceKind, kind);

        // a slide shows "page n" of the deck: it keeps its place and notes while the deck still has page n. Pages the
        // author deleted from the presentation stay deleted; only pages beyond the old count are new
        var slides = await db.PresentationSlides.Where(s => s.PdfFileId == file.Id).ToListAsync(cancellationToken);
        var dropped = slides.Where(s => s.PageNumber is not { } page || page > info.PageCount).ToList();
        var kept = slides.Except(dropped).ToList();
        var added = Enumerable.Range(file.PageCount + 1, Math.Max(0, info.PageCount - file.PageCount)).ToList();
        var total = await db.PresentationSlides.CountAsync(s => s.PresentationId == presentation.Id, cancellationToken);
        RequireRoom(total - dropped.Count, added.Count);

        var previous = file.FileNameSaved;
        var fileName = await storage.SaveAsync(pdf, cancellationToken);
        try
        {
            db.PresentationSlides.RemoveRange(dropped); // soft-delete
            if (added.Count > 0)
            {
                // new pages follow the deck's last slide in the presentation, or close the presentation when none is left
                var anchor = kept.Count > 0
                    ? kept.Max(s => s.Sorting)
                    : await db.PresentationSlides.Where(s => s.PresentationId == presentation.Id)
                        .MaxAsync(s => (int?)s.Sorting, cancellationToken) ?? 0;
                var later = await db.PresentationSlides
                    .Where(s => s.PresentationId == presentation.Id && s.Sorting > anchor).ToListAsync(cancellationToken);
                foreach (var slide in later)
                {
                    slide.Sorting += added.Count;
                }
                db.PresentationSlides.AddRange(added.Select((page, i) => PageSlide(file, page, anchor + i + 1)));
            }
            file.FileNameSaved = fileName;
            file.SizeBytes = pdf.Length;
            file.PageCount = info.PageCount;
            file.Revision++;
            file.Title = Cap(sourceName ?? info.Title ?? BaseName(uploadName) ?? file.Title, 300);
            Stamp(file, kind, sourceId, actor);
            presentation.ModifiedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            storage.Delete(fileName);
            if (ex is DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException(ChangedMeanwhile);
            }
            throw;
        }
        if (dropped.Count > 0)
        {
            await PresentationService.RenumberAsync(db, presentation.Id, cancellationToken);
        }
        DeleteStored(previous);
        ReportSlides(presentation.Id);
        return new SlidesRefreshResult(info.PageCount, added.Count, dropped.Count);
    }

    public async Task RemoveAsync(string fileId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequirePresentationWrite(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var (file, presentation) = await LoadManagedFileAsync(db, fileId, actor, cancellationToken);
        var slides = await db.PresentationSlides.Where(s => s.PdfFileId == file.Id).ToListAsync(cancellationToken);
        db.PresentationSlides.RemoveRange(slides); // soft-delete
        db.PresentationFiles.Remove(file);
        presentation.ModifiedAt = DateTime.UtcNow;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException(ChangedMeanwhile);
        }
        await PresentationService.RenumberAsync(db, presentation.Id, cancellationToken);
        DeleteStored(file.FileNameSaved);
        ReportSlides(presentation.Id);
    }

    /// <summary>The PDF, where it came from, what it holds and, through the sign-in, the deck's name in Drive. The
    /// quota is taken once the source is plausible - a mistyped link or an upload without a PDF header costs none - and
    /// before the file is parsed, so every parse costs its author a slot.</summary>
    private async Task<(ImportSourceKind Kind, string? Id, byte[] Pdf, PdfInfo Info, string? Name)> ResolveSourceAsync(
        string? link, GoogleDrivePick? drive, byte[]? upload, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(link))
        {
            var fileId = GoogleLinks.FileId(link, GoogleFileKind.Presentation)
                ?? throw new GoogleExportException(
                    "Das ist kein Link auf eine Google-Präsentation (https://docs.google.com/presentation/d/…).");
            GoogleImportQuota.Take(actor);
            var pdf = await fetcher.FetchPresentationPdfAsync(fileId, storage.MaxBytes, cancellationToken);
            return (ImportSourceKind.Link, fileId, pdf, await inspector.InspectAsync(pdf, cancellationToken), null);
        }
        if (drive is not null)
        {
            GoogleExportFetcher.RequireUsable(drive, fetcher.IsDriveConfigured);
            GoogleImportQuota.Take(actor);
            var export = await fetcher.FetchDrivePresentationPdfAsync(drive, storage.MaxBytes, cancellationToken);
            return (ImportSourceKind.Drive, drive.FileId, export.Bytes,
                await inspector.InspectAsync(export.Bytes, cancellationToken), export.Name);
        }
        if (upload is { Length: > 0 })
        {
            if (upload.Length > storage.MaxBytes)
            {
                throw new GoogleExportException($"Die Datei ist zu groß (höchstens {storage.MaxBytes / 1024 / 1024} MB).");
            }
            if (!PdfInspector.HasHeader(upload))
            {
                throw new GoogleExportException(PdfInspector.NotAPdf);
            }
            GoogleImportQuota.Take(actor);
            return (ImportSourceKind.Upload, null, upload, await inspector.InspectAsync(upload, cancellationToken), null);
        }
        throw new InvalidOperationException("Gib einen Freigabe-Link an, wähle die Präsentation über Google oder lade die PDF-Datei hoch.");
    }

    private static async Task<(PresentationFile File, Presentation Presentation)> LoadManagedFileAsync(AppDbContext db,
        string fileId, ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var file = await db.PresentationFiles.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken)
            ?? throw new InvalidOperationException("Import nicht gefunden.");
        var presentation = await PresentationService.LoadManagedAsync(db, file.PresentationId, actor, cancellationToken);
        return (file, presentation);
    }

    private static PresentationSlide PageSlide(PresentationFile file, int page, int sorting) => new()
    {
        PresentationId = file.PresentationId,
        Layout = SlideLayout.PdfPage,
        PdfFileId = file.Id,
        PageNumber = page,
        Sorting = sorting,
    };

    private static void RequireRoom(int existing, int adding)
    {
        if (existing + adding > PresentationService.MaxSlides)
        {
            throw new InvalidOperationException(adding > 1
                ? $"Eine Präsentation hat höchstens {PresentationService.MaxSlides} Folien; mit diesem Import wären es {existing + adding}."
                : $"Eine Präsentation hat höchstens {PresentationService.MaxSlides} Folien.");
        }
    }

    private static void Stamp(PresentationFile file, ImportSourceKind kind, string? sourceId, ClaimsPrincipal actor)
    {
        file.SourceKind = kind;
        file.SourceId = sourceId;
        file.ImportedAt = DateTime.UtcNow;
        file.ImportedById = actor.GetAgentId();
    }

    /// <summary>Removes a replaced or removed PDF after the commit; best effort, leftover bytes are housekeeping.</summary>
    private void DeleteStored(string fileName)
    {
        try
        {
            storage.Delete(fileName);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the presentation file {FileName} failed.", fileName);
        }
    }

    /// <summary>Tells the live pages of a running deck to load its slides again.</summary>
    private void ReportSlides(string presentationId)
    {
        if (live.Get(presentationId) is not null)
        {
            broadcaster.Report(presentationId, PresentationLiveEvent.SlidesChanged);
        }
    }

    private static string? BaseName(string? uploadName)
        => Path.GetFileNameWithoutExtension(uploadName ?? string.Empty).Trim() is { Length: > 0 } name ? name : null;

    private static string Cap(string value, int length) => value.Length > length ? value[..length].TrimEnd() : value;
}
