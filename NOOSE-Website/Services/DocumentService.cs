using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

public class DocumentService(IDbContextFactory<AppDbContext> dbFactory) : IDocumentService
{
    public async Task<List<DocumentListItem>> GetListAsync(DocumentViewerScope scope, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var baseQuery = partnerAgency is { } agency
            ? db.Documents.OnlyPartnerVisible(db, agency, partnerAgentId)
            : db.Documents.OnlyVisible(db, scope);
        var rows = await baseQuery
            .OrderByDescending(d => d.Pinned)
            .ThenByDescending(d => d.ModifiedAt ?? d.CreatedAt)
            .Select(d => new DocumentRow(d.Id, d.Title, d.Category, d.IsClassified, d.IsTRUClassified, d.IsHRBClassified,
                d.ModifiedAt ?? d.CreatedAt, d.Pinned))
            .ToListAsync(cancellationToken);
        return rows.Select(ToListItem).ToList();
    }

    public async Task<List<DocumentListItem>> SearchAsync(string? searchText, DocumentViewerScope scope, int max = 20, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var query = db.Documents.OnlyVisible(db, scope);

        var s = searchText?.Trim();
        if (!string.IsNullOrWhiteSpace(s))
        {
            query = query.Where(d => d.Title.Contains(s) || (d.Category != null && d.Category.Contains(s)));
        }

        var rows = await query
            .OrderByDescending(d => d.ModifiedAt ?? d.CreatedAt)
            .Take(max)
            .Select(d => new DocumentRow(d.Id, d.Title, d.Category, d.IsClassified, d.IsTRUClassified, d.IsHRBClassified,
                d.ModifiedAt ?? d.CreatedAt, d.Pinned))
            .ToListAsync(cancellationToken);
        return rows.Select(ToListItem).ToList();
    }

    private sealed record DocumentRow(string Id, string Title, string? Category,
        bool IsClassified, bool IsTRUClassified, bool IsHRBClassified, DateTime Refreshed, bool Pinned);

    private static DocumentListItem ToListItem(DocumentRow d)
    {
        var classification = d.IsClassified ? DocumentClassification.Leadership
            : d.IsTRUClassified ? DocumentClassification.Tru
            : d.IsHRBClassified ? DocumentClassification.Hrb
            : DocumentClassification.None;
        return new DocumentListItem(d.Id, d.Title, d.Category, classification, d.Refreshed, d.Pinned);
    }

    public async Task<Document?> GetAsync(string id, DocumentViewerScope scope, CancellationToken cancellationToken = default, PartnerAgency? partnerAgency = null, string? partnerAgentId = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (partnerAgency is { } agency)
        {
            return await PartnerVisibility.IsRecordVisibleToPartnerAsync(db, nameof(Document), id, agency, partnerAgentId, cancellationToken)
                ? await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
                : null;
        }
        // one gate: secrecy level, owning taskforce and per-agent revocation. Null hides existence either way.
        return await db.Documents.OnlyVisible(db, scope).FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    }

    public async Task<Document> CreateAsync(DocumentInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var title = (input.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Ein Titel ist erforderlich.");
        }

        Permission.RequireMayAssignClassification(actor, input.Classification);

        var document = new Document
        {
            Title = title,
            Category = input.Category.TrimToNull(),
            ContentHtml = HtmlCleanup.Clean(input.ContentHtml),
            Classification = input.Classification,
            OwnerTaskforceId = input.OwnerTaskforceId.TrimToNull(),
        };

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        db.Documents.Add(document);
        await db.SaveChangesAsync(cancellationToken);
        return document;
    }

    public async Task RefreshAsync(string id, DocumentInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var title = (input.Title ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new InvalidOperationException("Ein Titel ist erforderlich.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Dokument nicht gefunden.");

        // partners may only edit documents they authored themselves
        if (actor.IsPartner() && document.CreatedById != actor.GetAgentId())
        {
            throw new UnauthorizedAccessException("Partner dürfen nur selbst erstellte Dokumente bearbeiten.");
        }

        var scope = DocumentViewerScope.From(actor);
        if (!scope.CanSee(document.Classification))
        {
            throw new UnauthorizedAccessException("Dieses Dokument ist eine Verschlusssache und dir nicht zugänglich.");
        }
        Permission.RequireMayAssignClassification(actor, input.Classification);

        document.Title = title;
        document.Category = input.Category.TrimToNull();
        // an imported original keeps its text: the editor shows it locked, and a stale editor payload must not
        // replace the copy that belongs to the original still on display
        if (document.ImportedHtml is null)
        {
            document.ContentHtml = HtmlCleanup.Clean(input.ContentHtml);
        }
        document.Classification = input.Classification;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task PinSetAsync(string id, bool pinned, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // bypass audit interceptor
        var affected = await db.Documents
            .Where(d => d.Id == id)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.Pinned, pinned), cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException("Dokument nicht gefunden.");
        }

        // audit manually: the ExecuteUpdate above bypassed the interceptor
        db.AuditLogs.Add(ManualAudit.Row(nameof(Document), id, AuditAction.Modified, actor,
            ManualAudit.Change("Angepinnt", null, pinned)));
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var document = await db.Documents.FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (document is null)
        {
            return;
        }

        var isCreator = document.CreatedById is not null && document.CreatedById == actor.GetAgentId();
        if (!isCreator && !actor.IsLeadership())
        {
            throw new UnauthorizedAccessException("Nur der Ersteller oder die Führung darf dieses Dokument löschen.");
        }

        db.Documents.Remove(document);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<DocumentAttachment>> GetAttachmentsAsync(string documentId, bool isLeadership, string? meId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var sources = await db.Sources
            .Where(q => q.Type == SourceType.Document && q.TargetType == nameof(Document) && q.TargetId == documentId)
            .Select(q => new { q.EntityType, q.EntityId })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (sources.Count == 0)
        {
            return new();
        }

        var refs = sources.Select(q => (q.EntityType, q.EntityId)).ToList();
        var map = await RecordsReference.ResolveAsync(db, refs, cancellationToken, mayAllTaskforces: isLeadership, meId: meId);

        var result = new List<DocumentAttachment>();
        foreach (var q in sources)
        {
            if (map.TryGetValue((q.EntityType, q.EntityId), out var a))
            {
                if (!isLeadership && a.Classified)
                {
                    continue;
                }
                result.Add(new DocumentAttachment(q.EntityType, q.EntityId, a.Display, a.Href));
            }
        }
        return result;
    }
}
