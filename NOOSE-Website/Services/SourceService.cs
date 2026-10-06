using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ISourceService" />
public class SourceService(
    IDbContextFactory<AppDbContext> dbFactory, ISourcesStorageService storage, INotificationService notifications)
    : ISourceService
{
    public async Task<List<Source>> GetForRecordAsync(string entityType, string entityId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // parent visibility (scope carries meId for taskforce membership)
        if (!await Visibility.IsRecordVisibleAsync(db, entityType, entityId, scope, cancellationToken))
        {
            return new();
        }

        var sources = await db.Sources
            .Where(q => q.EntityType == entityType && q.EntityId == entityId)
            .OrderByDescending(q => q.Pinned)
            .ThenByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
        if (scope.PartnerAgency is { } agency)
        {
            if (await PartnerVisibility.IsContentBlockedAsync(db, agency, nameof(Source), cancellationToken))
            {
                return new();
            }
            var kept = new HashSet<string>();
            // document sources: keep those whose target document the partner may see (own-authored or released, not classified)
            foreach (var s in sources.Where(q => q.Type == SourceType.Document && q.TargetId != null))
            {
                if (await PartnerVisibility.IsRecordVisibleToPartnerAsync(
                        db, nameof(Document), s.TargetId!, agency, scope.MeId, cancellationToken))
                {
                    kept.Add(s.Id);
                }
            }
            // other sources keep the child-release filter; internal cross-refs stay hidden
            var others = sources.Where(q => q.Type != SourceType.Internal && q.Type != SourceType.Document).ToList();
            others = await PartnerVisibility.FilterChildrenAsync(db, entityType, entityId, nameof(Source), others, q => q.Id, agency, scope.MeId, cancellationToken);
            foreach (var s in others) { kept.Add(s.Id); }
            // preserve original pinned/date ordering
            sources = sources.Where(q => kept.Contains(q.Id)).ToList();
        }

        // "nur intern" sources: taskforce members only on a taskforce, never a partner anywhere
        if (sources.Any(s => s.IsInternalOnly))
        {
            var myTaskforces = await SourceVisibility.MyTaskforceIdsAsync(db, scope, cancellationToken);
            sources = sources.OnlyVisible(entityType, entityId, scope, myTaskforces);
        }

        return sources;
    }

    public async Task<Source> CreateAsync(string entityType, string entityId, SourceInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var title = input.Title?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Ein Titel ist erforderlich.");
        }

        // partners may only contribute documents they create — never uploads, links or cross-references
        if (actor.IsPartner() && input.Type != SourceType.Document)
        {
            throw new UnauthorizedAccessException("Partner können nur Dokumente als Quelle hinzufügen.");
        }

        await using var visDb = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await Visibility.IsRecordVisibleAsync(visDb, entityType, entityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }

        var source = new Source
        {
            EntityType = entityType,
            EntityId = entityId,
            Type = input.Type,
            Title = title,
            Description = input.Description.TrimToNull(),
            IsInternalOnly = input.IsInternalOnly,
        };

        switch (input.Type)
        {
            case SourceType.Link:
                if (string.IsNullOrWhiteSpace(input.Url))
                {
                    throw new InvalidOperationException("Bei einer Link-Quelle ist eine URL erforderlich.");
                }
                var url = input.Url.Trim();
                // http(s) only, blocks stored-XSS via javascript: links
                if (!IsSafeUrl(url))
                {
                    throw new InvalidOperationException("Bitte eine gültige http(s)-URL angeben.");
                }
                source.Url = url;
                break;

            case SourceType.Internal:
                if (string.IsNullOrWhiteSpace(input.TargetType) || string.IsNullOrWhiteSpace(input.TargetId))
                {
                    throw new InvalidOperationException("Bei einer internen Quelle ist eine Ziel-Akte erforderlich.");
                }
                source.TargetType = input.TargetType;
                source.TargetId = input.TargetId;
                break;

            case SourceType.Upload:
                if (input.FileContent is null || input.FileContent.Length == 0)
                {
                    throw new InvalidOperationException("Es wurde keine Datei ausgewählt.");
                }
                // enforce size limit server-side
                if (input.FileContent.Length > storage.MaxBytes)
                {
                    throw new InvalidOperationException($"Datei zu groß (max. {storage.MaxBytes / (1024 * 1024)} MB).");
                }
                if (!storage.IsAllowedType(input.ContentType ?? string.Empty))
                {
                    throw new InvalidOperationException($"Dateityp „{input.ContentType}“ ist nicht erlaubt.");
                }
                await using (var ms = new MemoryStream(input.FileContent))
                {
                    source.FileNameSaved = await storage.SaveAsync(ms, input.OriginalName ?? "datei", cancellationToken);
                }
                source.OriginalName = input.OriginalName;
                source.ContentType = input.ContentType;
                source.SizeBytes = input.SizeBytes;
                break;

            case SourceType.FreeText:
                break;

            case SourceType.Document:
                if (string.IsNullOrWhiteSpace(input.TargetId))
                {
                    throw new InvalidOperationException("Bei einer Dokument-Quelle ist ein Ziel-Dokument erforderlich.");
                }
                // verify referenced document is visible; no existence leak of classified docs
                await using (var checkDb = await dbFactory.CreateDbContextAsync(cancellationToken))
                {
                    var classifiedFlag = await checkDb.Documents
                        .Where(d => d.Id == input.TargetId)
                        .Select(d => (bool?)d.IsClassified)
                        .FirstOrDefaultAsync(cancellationToken);
                    if (classifiedFlag is null || (classifiedFlag == true && !actor.IsLeadership()))
                    {
                        throw new InvalidOperationException("Das gewählte Dokument wurde nicht gefunden.");
                    }
                }
                source.TargetType = nameof(Document);
                source.TargetId = input.TargetId;
                break;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Sources.Add(source);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch when (source.Type == SourceType.Upload && source.FileNameSaved is not null)
        {
            // insert failed, remove the already-written file
            storage.Delete(source.FileNameSaved);
            throw;
        }

        await MentionNotify.DeltaAsync(notifications, null, source.Description, "einer Quelle",
            entityType, entityId, actor, cancellationToken, childType: nameof(Source));

        return source;
    }

    public async Task RemoveAsync(string sourceId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Sources.FirstOrDefaultAsync(q => q.Id == sourceId, cancellationToken);
        if (source is null)
        {
            return;
        }
        if (!await Visibility.IsRecordVisibleAsync(db, source.EntityType, source.EntityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        // soft delete keeps the file for restore; removed on hard delete
        db.Sources.Remove(source);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PinSetAsync(string sourceId, bool pinned, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate bypasses the interceptors, so enforce write access here
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Sources.FirstOrDefaultAsync(q => q.Id == sourceId, cancellationToken)
            ?? throw new InvalidOperationException("Quelle nicht gefunden.");
        if (!await Visibility.IsRecordVisibleAsync(db, source.EntityType, source.EntityId, ViewerScope.From(actor), cancellationToken))
        {
            throw new UnauthorizedAccessException("Diese Akte ist für dich nicht zugänglich.");
        }
        // ExecuteUpdate on purpose: pinning is not a content edit, skip the modified stamp
        await db.Sources
            .Where(q => q.Id == sourceId)
            .ExecuteUpdateAsync(s => s.SetProperty(q => q.Pinned, pinned), cancellationToken);

        // audit manually: the ExecuteUpdate bypassed the interceptor
        db.AuditLogs.Add(ManualAudit.Row(nameof(Source), sourceId, AuditAction.Modified, actor,
            ManualAudit.Change("Angepinnt", null, pinned)));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<Source?> GetForDownloadAsync(string sourceId, ViewerScope scope, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = await db.Sources.FirstOrDefaultAsync(q => q.Id == sourceId, cancellationToken);
        if (source is null || source.Type != SourceType.Upload || string.IsNullOrEmpty(source.FileNameSaved))
        {
            return null;
        }
        if (scope.PartnerAgency is { } agency)
        {
            // partners: parent visible AND (whole-record or this source released)
            return await PartnerVisibility.IsChildVisibleToPartnerAsync(db, source.EntityType, source.EntityId, nameof(Source), sourceId, agency, scope.MeId, cancellationToken)
                ? source
                : null;
        }
        return await Visibility.IsRecordVisibleAsync(db, source.EntityType, source.EntityId, scope, cancellationToken)
            ? source
            : null;
    }

    /// <summary>Allows only absolute http(s) URLs (blocks javascript:/data: links).</summary>
    private static bool IsSafeUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

}
