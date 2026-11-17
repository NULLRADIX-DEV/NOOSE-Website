using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Cases;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IApplicationCaseService" />
public class ApplicationCaseService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRecruitingAutomationService automation,
    ICaseService cases,
    IDocumentService documents,
    IDocumentTemplateService templates,
    ISourceService sources,
    IPlaceholderService placeholders,
    ILinkService links,
    ILogger<ApplicationCaseService> logger) : IApplicationCaseService
{
    private const string SecurityCheckTitlePrefix = "Sicherheitsüberprüfung | ";

    public async Task EnsureSecurityCheckCaseAsync(string bewerbungId, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        // read-only supervisors never provision records
        if (!actor.MayWrite())
        {
            return;
        }

        var config = await automation.GetAsync(cancellationToken);
        if (!config.AutoCaseEnabled)
        {
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var bewerbung = await db.Bewerbungen.AsNoTracking()
            .FirstOrDefaultAsync(b => b.Id == bewerbungId, cancellationToken);
        if (bewerbung is null)
        {
            return;
        }
        if (bewerbung.LinkedCaseId is { } existingCaseId)
        {
            // provisioned before the SÜ was linked to the application: link it now
            await LinkExistingSecurityCheckAsync(db, bewerbungId, existingCaseId, actor, cancellationToken);
            return;
        }

        var name = bewerbung.Name;

        var @case = await cases.CreateAsync(new CaseInput
        {
            Title = $"Bewerbungsverfahren | {name}",
            Status = CaseStatus.Open,
            Classification = Classification.Unknown,
            // recruiting files stay inside HRB
            SecrecyLevel = DocumentClassification.Hrb,
        }, actor, cancellationToken);

        // claim the application atomically: a concurrent assignment (or double-click) that already
        // linked its own case updates 0 rows here, so it discards this duplicate and exactly one wins
        var claimed = await db.Bewerbungen
            .Where(b => b.Id == bewerbungId && b.LinkedCaseId == null)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.LinkedCaseId, @case.Id), cancellationToken);
        if (claimed == 0)
        {
            // lost the race: soft-delete this duplicate case + its auto-added lead row, and log the discard
            // (CaseService.DeleteAsync is leadership-gated, but a plain HRB writer created it here; MayWrite enforced above)
            await db.Cases.Where(v => v.Id == @case.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.IsDeleted, true)
                    .SetProperty(v => v.DeletedAt, DateTime.UtcNow)
                    .SetProperty(v => v.DeletedById, actor.GetAgentId()), cancellationToken);
            await db.CaseAgents.Where(a => a.CaseId == @case.Id).ExecuteDeleteAsync(cancellationToken);
            db.AuditLogs.Add(ManualAudit.Row(nameof(Case), @case.Id, AuditAction.Deleted, actor));
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        // 2) document from the configured template (built-in body as fallback if it was deleted)
        var template = await templates.GetAsync(config.TemplateId, cancellationToken);
        var body = template?.ContentHtml ?? ApplicationTemplates.SecurityCheckBody;
        var html = await placeholders.ApplyAsync(body, nameof(Case), @case.Id, actor, cancellationToken);
        var docTitle = $"{SecurityCheckTitlePrefix}{name}";
        var document = await documents.CreateAsync(new DocumentInput
        {
            Title = docTitle,
            ContentHtml = html,
            Classification = DocumentClassification.Hrb,
        }, actor, cancellationToken);

        // 3) file the document under the case
        await sources.CreateAsync(nameof(Case), @case.Id, new SourceInput
        {
            Type = SourceType.Document,
            Title = docTitle,
            TargetType = nameof(Document),
            TargetId = document.Id,
        }, actor, cancellationToken);

        // 4) and on the application itself, under its linked records
        await links.CreateAsync(nameof(Bewerbung), bewerbungId, nameof(Document), document.Id, null, actor,
            cancellationToken: cancellationToken);

        logger.LogInformation("Auto-provisioned case {CaseId} + document {DocId} for application {AppId}.",
            @case.Id, document.Id, bewerbungId);
    }

    private async Task LinkExistingSecurityCheckAsync(AppDbContext db, string bewerbungId, string caseId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var documentId = await db.Sources.AsNoTracking()
            .Where(s => s.EntityType == nameof(Case) && s.EntityId == caseId
                && s.Type == SourceType.Document && s.TargetType == nameof(Document)
                && s.Title.StartsWith(SecurityCheckTitlePrefix))
            .OrderBy(s => s.CreatedAt)
            .Select(s => s.TargetId)
            .FirstOrDefaultAsync(cancellationToken);
        if (documentId is null)
        {
            return;
        }

        var linked = await db.Links.AnyAsync(l => l.Kind == LinkKind.Default
            && ((l.SourceType == nameof(Bewerbung) && l.SourceId == bewerbungId
                    && l.TargetType == nameof(Document) && l.TargetId == documentId)
                || (l.SourceType == nameof(Document) && l.SourceId == documentId
                    && l.TargetType == nameof(Bewerbung) && l.TargetId == bewerbungId)),
            cancellationToken);
        if (!linked)
        {
            await links.CreateAsync(nameof(Bewerbung), bewerbungId, nameof(Document), documentId, null, actor,
                cancellationToken: cancellationToken);
        }
    }
}
