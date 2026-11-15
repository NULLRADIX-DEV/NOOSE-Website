using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Investigations;
using NOOSE_Website.Models.Investigations;

namespace NOOSE_Website.Services;

// Pictures on evidence: they share the evidence tab's rights and the limits of the piece they belong to.
public partial class InvestigationService
{
    /// <summary>Most pictures one piece of evidence holds.</summary>
    public const int MaxPhotosPerEvidence = 30;

    private static async Task<List<InvestigationEvidenceView>> EvidenceAsync(AppDbContext db, string id, Func<string, bool> shows,
        CancellationToken cancellationToken)
    {
        var rows = (await db.InvestigationEvidence.AsNoTracking()
                .Where(e => e.InvestigationId == id).OrderBy(e => e.Number).ToListAsync(cancellationToken))
            .Where(e => shows(e.Id)).ToList();
        var photos = (await db.InvestigationEvidencePhotos.AsNoTracking()
                .Where(p => p.InvestigationId == id).OrderBy(p => p.CreatedAt)
                .Select(p => new { p.Id, p.EvidenceId, p.OriginalName })
                .ToListAsync(cancellationToken))
            .ToLookup(p => p.EvidenceId);
        return rows.Select(e => new InvestigationEvidenceView(e.Id, e.Number, e.Name, e.Description, e.Owner, e.Officer, e.Status, e.SecuredAt,
                photos[e.Id].Select(p => new InvestigationPhotoView(p.Id, p.OriginalName)).ToList()))
            .ToList();
    }

    public long PhotoMaxBytes => photoStorage.MaxBytes;

    public bool IsAllowedPhotoType(string contentType) => photoStorage.IsAllowedType(contentType);

    public async Task<InvestigationEvidencePhoto?> GetEvidencePhotoAsync(string photoId, ClaimsPrincipal viewer, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var photo = await db.InvestigationEvidencePhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == photoId, cancellationToken);
        if (photo is null)
        {
            return null;
        }
        // the picture belongs to its piece of evidence: no read on the tab or a piece limited to others hides it
        var access = await InvestigationAccess.ResolveAsync(db, photo.InvestigationId, viewer, cancellationToken);
        if (!access.CanRead(InvestigationTabs.Evidence)
            || !await db.InvestigationEvidence.AnyAsync(e => e.Id == photo.EvidenceId, cancellationToken)
            || (await InvestigationAccess.HiddenItemIdsAsync(db, photo.InvestigationId, nameof(InvestigationEvidence), access, cancellationToken))
                .Contains(photo.EvidenceId))
        {
            return null;
        }
        return photo;
    }

    public async Task<string> AddEvidencePhotoAsync(string id, string evidenceId, Stream content, string originalName, string contentType, long size,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!photoStorage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException("Nur Bilder (JPG, PNG, WebP, GIF) lassen sich anhängen.");
        }
        if (size > photoStorage.MaxBytes)
        {
            throw new InvalidOperationException($"Das Bild ist zu groß (höchstens {photoStorage.MaxBytes / (1024 * 1024)} MB).");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Evidence);
        if (!await db.InvestigationEvidence.AnyAsync(e => e.Id == evidenceId && e.InvestigationId == id, cancellationToken))
        {
            throw new KeyNotFoundException("Dieses Beweismittel gibt es nicht.");
        }
        await RequireNotHiddenAsync(db, id, nameof(InvestigationEvidence), evidenceId, access, cancellationToken);
        if (await db.InvestigationEvidencePhotos.CountAsync(p => p.EvidenceId == evidenceId, cancellationToken) >= MaxPhotosPerEvidence)
        {
            throw new InvalidOperationException($"Ein Beweismittel trägt höchstens {MaxPhotosPerEvidence} Bilder.");
        }

        var fileName = await photoStorage.SaveAsync(content, contentType, cancellationToken);
        var photo = new InvestigationEvidencePhoto
        {
            InvestigationId = id,
            EvidenceId = evidenceId,
            FileNameSaved = fileName,
            OriginalName = Clip(Path.GetFileName(originalName), 255) ?? "Bild",
            ContentType = contentType,
            SizeBytes = size,
        };
        db.InvestigationEvidencePhotos.Add(photo);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            // no row, no file: a failed insert must not leave an orphan behind
            photoStorage.Delete(fileName);
            throw;
        }
        return photo.Id;
    }

    public async Task RemoveEvidencePhotoAsync(string id, string photoId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var access = await InvestigationAccess.ResolveAsync(db, id, actor, cancellationToken);
        InvestigationAccess.RequireEdit(access, InvestigationTabs.Evidence);
        var photo = await db.InvestigationEvidencePhotos.FirstOrDefaultAsync(p => p.Id == photoId && p.InvestigationId == id, cancellationToken)
            ?? throw new KeyNotFoundException("Dieses Bild gibt es nicht.");
        await RequireNotHiddenAsync(db, id, nameof(InvestigationEvidence), photo.EvidenceId, access, cancellationToken);
        // soft delete like every row of the file: the file stays, so the history and a restore keep their picture
        db.Remove(photo);
        await db.SaveChangesAsync(cancellationToken);
    }
}
