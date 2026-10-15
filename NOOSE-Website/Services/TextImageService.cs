using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Infrastructure.Presentations;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc />
public class TextImageService(
    IDbContextFactory<AppDbContext> dbFactory,
    ITextImageStorageService storage,
    PresentationLiveRegistry live) : ITextImageService
{
    public async Task<string?> SaveAsync(string entityType, string entityId, byte[] content, string contentType,
        ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // write guard before anything is written to disk: the read-only supervision must not leave a file behind
        Permission.RequireWriteAccess(actor);
        if (string.IsNullOrWhiteSpace(entityType) || string.IsNullOrWhiteSpace(entityId)
            || content.Length == 0 || content.Length > storage.MaxBytes || !storage.IsAllowedType(contentType))
        {
            return null;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // the record has to be one the pasting agent may see, or a picture could be parked under a foreign case
        if (!await MaySeeOwnerAsync(db, entityType, entityId, actor, cancellationToken))
        {
            return null;
        }

        var fileName = await storage.SaveAsync(new MemoryStream(content), contentType, cancellationToken);
        var row = new TextImage
        {
            EntityType = entityType,
            EntityId = entityId,
            FileNameSaved = fileName,
            ContentType = contentType,
            SizeBytes = content.Length,
        };
        db.TextImages.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return MentionParser.Token(nameof(TextImage), row.Id);
    }

    public async Task<TextImageAccess?> GetAccessAsync(string id, ClaimsPrincipal user,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.TextImages.AsNoTracking()
            .Where(b => b.Id == id)
            .Select(b => new { b.EntityType, b.EntityId, b.FileNameSaved, b.ContentType })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return null;
        }
        return await MaySeeOwnerAsync(db, row.EntityType, row.EntityId, user, cancellationToken)
            || await InvitedToSlideAsync(db, row.EntityType, row.EntityId, user, cancellationToken)
            ? new TextImageAccess(row.FileNameSaved, row.ContentType)
            : null;
    }

    /// <summary>A picture on a slide also reaches an agent invited to the running session of its presentation.</summary>
    /// <remarks>Read only: pasting a picture still needs the slide itself, see <see cref="SaveAsync"/>.</remarks>
    private async Task<bool> InvitedToSlideAsync(AppDbContext db, string entityType, string entityId, ClaimsPrincipal user,
        CancellationToken cancellationToken)
    {
        if (entityType != nameof(PresentationSlide))
        {
            return false;
        }
        var presentationId = await db.PresentationSlides.AsNoTracking().Where(s => s.Id == entityId)
            .Select(s => s.PresentationId).FirstOrDefaultAsync(cancellationToken);
        return presentationId is not null
            && await PresentationLiveAccess.IsInvitedViewerAsync(db, live, presentationId, user, cancellationToken);
    }

    /// <summary>Whether the viewer may see the record the picture hangs on; the token itself grants nothing.</summary>
    /// <remarks>
    /// Routed through <see cref="RecordsReference"/> so a new record type inherits the gate instead of needing one:
    /// a reference that does not resolve — soft-deleted, or a taskforce the viewer is not in — is a miss, and a
    /// classified record needs the classified read. Two types cannot answer through it: a library document carries
    /// three secrecy flags plus per-agent revocation, and a personal file carries no flag at all although only
    /// leadership may read it. A rich-text carrier answers through <see cref="Visibility"/> instead, and only
    /// when its type is registered — anything else is a miss rather than a fall-through.
    /// </remarks>
    private static async Task<bool> MaySeeOwnerAsync(AppDbContext db, string entityType, string entityId,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (entityType == nameof(Document))
        {
            return await db.Documents.OnlyVisible(db, DocumentViewerScope.From(user))
                .AnyAsync(d => d.Id == entityId, cancellationToken);
        }
        if (entityType == nameof(Agent))
        {
            return user.IsLeadership();
        }
        if (RichTextImageFields.IsRegistered(entityType))
        {
            return await Visibility.IsRecordVisibleAsync(db, entityType, entityId, ViewerScope.From(user), cancellationToken);
        }

        var map = await RecordsReference.ResolveAsync(db, [(entityType, entityId)], cancellationToken,
            mayAllTaskforces: user.IsLeadership(), meId: user.GetAgentId());
        return map.TryGetValue((entityType, entityId), out var owner)
            && (!owner.Classified || user.MayClassifiedRead());
    }
}
