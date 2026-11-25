using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IApplicationCaseService" />
public class ApplicationCaseService(
    IDbContextFactory<AppDbContext> dbFactory,
    IRecruitingAutomationService automation,
    IDocumentService documents,
    IDocumentTemplateService templates,
    ISourceService sources,
    IPlaceholderService placeholders,
    ILogger<ApplicationCaseService> logger) : IApplicationCaseService
{
    private const string SecurityCheckTitlePrefix = "Sicherheitsüberprüfung | ";

    // assigning and starting the check can fire close together; one app instance, so a process gate keeps it to one SÜ
    private static readonly SemaphoreSlim Gate = new(1, 1);

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

        await Gate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var bewerbung = await db.Bewerbungen.AsNoTracking()
                .FirstOrDefaultAsync(b => b.Id == bewerbungId, cancellationToken);
            if (bewerbung is null || await HasSecurityCheckAsync(db, bewerbungId, cancellationToken))
            {
                return;
            }
            // older applications got a case with the SÜ under it: file that one instead of making a second
            if (bewerbung.LinkedCaseId is { } existingCaseId
                && await FileExistingSecurityCheckAsync(db, bewerbungId, existingCaseId, actor, cancellationToken))
            {
                return;
            }

            var template = await templates.GetAsync(config.TemplateId, cancellationToken);
            var body = template?.ContentHtml ?? ApplicationTemplates.SecurityCheckBody;
            var html = await placeholders.ApplyAsync(FillApplicationTokens(body, bewerbung), null, null, actor, cancellationToken);
            var docTitle = $"{SecurityCheckTitlePrefix}{bewerbung.Name}";
            var document = await documents.CreateAsync(new DocumentInput
            {
                Title = docTitle,
                ContentHtml = html,
                Classification = DocumentClassification.Hrb,
            }, actor, cancellationToken);

            await sources.CreateAsync(nameof(Bewerbung), bewerbungId, SecurityCheckSource(docTitle, document.Id), actor,
                cancellationToken);

            logger.LogInformation("Auto-provisioned security check document {DocId} for application {AppId}.",
                document.Id, bewerbungId);
        }
        finally
        {
            Gate.Release();
        }
    }

    // the placeholder service only knows record types; an application answers {{Name}} and {{Aktenzeichen}} itself
    private static string FillApplicationTokens(string body, Bewerbung bewerbung) => body
        .Replace("{{Name}}", WebUtility.HtmlEncode(bewerbung.Name), StringComparison.OrdinalIgnoreCase)
        .Replace("{{Aktenzeichen}}", WebUtility.HtmlEncode(bewerbung.CaseNumber), StringComparison.OrdinalIgnoreCase);

    private static Task<bool> HasSecurityCheckAsync(AppDbContext db, string bewerbungId, CancellationToken cancellationToken)
        => db.Sources.AsNoTracking().AnyAsync(s => s.EntityType == nameof(Bewerbung) && s.EntityId == bewerbungId
            && s.Type == SourceType.Document && s.TargetType == nameof(Document)
            && s.Title.StartsWith(SecurityCheckTitlePrefix), cancellationToken);

    private static SourceInput SecurityCheckSource(string title, string documentId) => new()
    {
        Type = SourceType.Document,
        Title = title,
        TargetType = nameof(Document),
        TargetId = documentId,
    };

    private async Task<bool> FileExistingSecurityCheckAsync(AppDbContext db, string bewerbungId, string caseId,
        ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var filed = await db.Sources.AsNoTracking()
            .Where(s => s.EntityType == nameof(Case) && s.EntityId == caseId
                && s.Type == SourceType.Document && s.TargetType == nameof(Document)
                && s.Title.StartsWith(SecurityCheckTitlePrefix))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new { s.Title, s.TargetId })
            .FirstOrDefaultAsync(cancellationToken);
        if (filed?.TargetId is null)
        {
            return false;
        }

        await sources.CreateAsync(nameof(Bewerbung), bewerbungId, SecurityCheckSource(filed.Title, filed.TargetId),
            actor, cancellationToken);
        return true;
    }
}
