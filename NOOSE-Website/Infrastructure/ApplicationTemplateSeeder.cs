using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;

namespace NOOSE_Website.Infrastructure;

/// <summary>Fixed identity + body of the auto-provisioned "Sicherheitsüberprüfung" case document template.</summary>
public static class ApplicationTemplates
{
    /// <summary>Stable id so the auto-provisioner can resolve the template without a name lookup.</summary>
    public const string SecurityCheckId = "b1e5c8a0-4d21-4f77-9c3e-8a2f6d0b91a4";

    /// <summary>General library category (never the recruiting "Bewerbung" scheme, so the picker lists it).</summary>
    public const string SecurityCheckCategory = "Bewerbungsverfahren";

    public const string SecurityCheckName = "Sicherheitsüberprüfung";

    /// <summary>Placeholder body ({{...}} tokens: {{Name}} is the case title "Bewerbungsverfahren | Name"); also the fallback when the template row was deleted.</summary>
    public const string SecurityCheckBody =
        "<p><strong>Federal Investigation Bureau</strong><br>Sicherheitsüberprüfung</p>" +
        "<p>Vorgang: {{Name}}<br>Aktenzeichen: {{Aktenzeichen}}<br>Datum: {{Datum}}</p>" +
        "<p><strong>Gegenstand der Sicherheitsüberprüfung</strong></p>" +
        "<p>Nachfolgend werden die im Rahmen dieses Bewerbungsverfahrens erhobenen Erkenntnisse, Prüfschritte " +
        "und Bewertungen zur bewerbenden Person dokumentiert.</p>" +
        "<p><strong>Erkenntnisse</strong></p><p>—</p>" +
        "<p><strong>Bewertung</strong></p><p>—</p>" +
        "<p>Bearbeitung: {{Agent}} ({{Dienstgrad}})</p>";
}

/// <summary>Seeds the "Sicherheitsüberprüfung" case-document template once, idempotently.</summary>
public static class ApplicationTemplateSeeder
{
    public static async Task SeedAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        // IgnoreQueryFilters revives a soft-deleted default on the next start
        var existing = await db.DocumentTemplates.IgnoreQueryFilters()
            .FirstOrDefaultAsync(t => t.Id == ApplicationTemplates.SecurityCheckId, cancellationToken);
        if (existing is not null)
        {
            if (existing.IsDeleted)
            {
                existing.IsDeleted = false;
                existing.DeletedAt = null;
                existing.DeletedById = null;
                await db.SaveChangesAsync(cancellationToken);
            }
            return;
        }

        db.DocumentTemplates.Add(new DocumentTemplate
        {
            Id = ApplicationTemplates.SecurityCheckId,
            Name = ApplicationTemplates.SecurityCheckName,
            Category = ApplicationTemplates.SecurityCheckCategory,
            ContentHtml = ApplicationTemplates.SecurityCheckBody,
            IsActive = true,
            Sorting = 0,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
