using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Data.Entities.Requests;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Release and classification requests: the requester's own, plus everything for the decision inbox.</summary>
public sealed class RequestSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Request);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner && viewer.MeId is { Length: > 0 };

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var meId = query.Scope.MeId;
        var inbox = query.Scope.MayClassifiedRead;
        var q = inbox ? db.Requests.AsQueryable() : db.Requests.Where(a => a.CreatedById == meId);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(a => (a.Justification != null && a.Justification.Contains(s))
                || (a.TargetDesignation != null && a.TargetDesignation.Contains(s))
                || (a.DecisionNote != null && a.DecisionNote.Contains(s)));
        }
        var raw = await q.OrderByDescending(a => a.CreatedAt).Take(query.PerCategory * 2)
            .Select(a => new { a.Id, a.TargetType, a.TargetId, a.TargetDesignation, a.Justification, a.Type, a.CreatedAt })
            .ToListAsync(cancellationToken);
        if (raw.Count == 0)
        {
            return [];
        }

        // the inbox arm reaches every request, so the target it names must still pass the target's own gate
        var parents = await SearchParentResolver.ResolveVisibleAsync(db,
            raw.Select(a => (a.TargetType, a.TargetId)).Distinct().ToList(), query.Viewer, null, cancellationToken, query.IncludeArchived);

        var hits = new List<SearchHit>();
        foreach (var a in raw)
        {
            var target = parents.TryGetValue((a.TargetType, a.TargetId), out var view) ? view.Title : null;
            if (target is null)
            {
                continue; // the request names a record the viewer may not see
            }
            hits.Add(new SearchHit(nameof(Request), a.Id,
                $"{RequestTypeDisplay.Name(a.Type)} · {target}", a.Justification ?? string.Empty, string.Empty)
            {
                Timestamp = a.CreatedAt,
                Href = "/admin/freigaben",
            });
            if (hits.Count >= query.PerCategory)
            {
                break;
            }
        }
        return hits;
    }
}

/// <summary>Monthly situation reports. Leadership only — they aggregate classified corpora.</summary>
public sealed class SituationReportSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(SituationReport);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.SituationReports.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(b => b.Title.Contains(s));
        }
        return await q.OrderByDescending(b => b.Year).ThenByDescending(b => b.Month).Take(query.PerCategory)
            .Select(b => new SearchHit(nameof(SituationReport), b.Id, b.Title,
                $"{b.Month:00}/{b.Year}", string.Empty) { Href = "/lageberichte/" + b.Id })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Applications. HRB or leadership, never the read-only supervision.</summary>
public sealed class BewerbungSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Bewerbung);

    public PartnerAccess Partner => PartnerAccess.Never;

    // MayRecruiting, not the looser point-check arm: that one admits the supervision, which must not see applicants
    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayRecruiting;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.Bewerbungen.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(b => b.Name.Contains(s) || b.CaseNumber.Contains(s)
                || (b.CoverLetter != null && b.CoverLetter.Contains(s))
                || (b.PriorExperience != null && b.PriorExperience.Contains(s)));
        }
        var rows = await q.OrderByDescending(b => b.SubmittedAt).Take(query.PerCategory)
            .Select(b => new { b.Id, b.Name, b.CaseNumber, b.Status, b.CoverLetter, b.SubmittedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(b => new SearchHit(nameof(Bewerbung), b.Id, b.Name,
                $"{BewerbungStatusDisplay.Name(b.Status)} · {SearchSnippet.Around(HtmlCleanup.PlainText(b.CoverLetter), query.Text)}",
                b.CaseNumber)
            {
                Timestamp = b.SubmittedAt,
            })
            .ToList();
    }
}

/// <summary>Internal messages on an application. The applicant-facing audience is deliberately excluded.</summary>
public sealed class BewerbungMessageSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(BewerbungMessage);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayRecruiting;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        // only the internal audience: an applicant-facing line carries an author the applicant must never learn,
        // and excluding those rows sidesteps the whole class rather than redacting per row
        var rows = await (
            from m in db.BewerbungMessages
            where m.Text.Contains(s) && m.Audience == BewerbungMessageAudience.Intern
            join b in db.Bewerbungen on m.BewerbungId equals b.Id
            orderby m.CreatedAt descending
            select new { m.Id, m.BewerbungId, m.Text, m.CreatedAt, b.Name, b.CaseNumber })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return rows.Select(m => new SearchHit(nameof(BewerbungMessage), m.BewerbungId, m.Name,
                HtmlCleanup.PlainText(m.Text), m.CaseNumber, nameof(Bewerbung)) { Timestamp = m.CreatedAt })
            .ToList();
    }
}

/// <summary>Application blocks.</summary>
public sealed class BewerbungssperreSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Bewerbungssperre);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayRecruiting;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // active only: an expired ban is gone from /bewerbungen?tab=sperren, so a hit there leads nowhere
        var q = db.Bewerbungssperren.Where(BewerbungssperreRules.Active(DateTime.UtcNow));
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(p => (p.Reason != null && p.Reason.Contains(s))
                || (p.ApplicantName != null && p.ApplicantName.Contains(s)));
        }
        return await q.OrderByDescending(p => p.CreatedAt).Take(query.PerCategory)
            .Select(p => new SearchHit(nameof(Bewerbungssperre), p.Id,
                p.ApplicantName ?? "Bewerbungssperre", p.Reason ?? string.Empty, string.Empty)
            {
                Timestamp = p.CreatedAt,
                Href = "/bewerbungen?tab=sperren",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Recruiting tests. Title only — the questions and options are the answer key.</summary>
public sealed class BewerbungTestSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(BewerbungTest);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayRecruiting;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.BewerbungTests.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Title.Contains(s));
        }
        return await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(BewerbungTest), t.Id, t.Title, string.Empty, string.Empty)
            {
                Href = "/bewerbungs-tests/" + t.Id,
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Keyword vocabulary.</summary>
public sealed class TagSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Tag);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.Tags.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s));
        }
        return await q.OrderBy(t => t.Name).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(Tag), t.Id, t.Name, string.Empty, string.Empty)
            {
                Href = "/einstellungen?tab=tags",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Training modules.</summary>
public sealed class TrainingModuleSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(TrainingModule);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.TrainingModules.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(m => m.Name.Contains(s) || (m.Description != null && m.Description.Contains(s)));
        }
        return await q.OrderBy(m => m.Sorting).Take(query.PerCategory)
            .Select(m => new SearchHit(nameof(TrainingModule), m.Id, m.Name, m.Description ?? string.Empty, string.Empty)
            {
                Href = "/einstellungen?tab=module",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Funding catalogue items.</summary>
public sealed class FinancingItemSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(FinancingItem);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.FinancingItems.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(i => i.Name.Contains(s)
                || (i.Category != null && i.Category.Contains(s))
                || (i.Description != null && i.Description.Contains(s)));
        }
        var rows = await q.OrderBy(i => i.Sorting).Take(query.PerCategory)
            .Select(i => new { i.Id, i.Name, i.Category, i.Description }).ToListAsync(cancellationToken);
        // joined in memory: MySQL cannot run the OUTER APPLY an array join in the projection becomes
        return rows.Select(i => new SearchHit(nameof(FinancingItem), i.Id, i.Name,
                string.Join(" · ", new[] { i.Category, i.Description }.Where(p => p != null)), string.Empty)
            {
                Href = "/finanzierungen?tab=katalog",
            })
            .ToList();
    }
}

/// <summary>Counter-intelligence rules. Leadership only, and never the read-only supervision — it would audit itself.</summary>
public sealed class CounterIntelRuleSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(CounterIntelRule);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.User.MayCounterIntel();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.CounterIntelRules.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(r => r.Name.Contains(s) || (r.Description != null && r.Description.Contains(s)));
        }
        return await q.OrderBy(r => r.Order).Take(query.PerCategory)
            .Select(r => new SearchHit(nameof(CounterIntelRule), r.Id, r.Name, r.Description ?? string.Empty, string.Empty)
            {
                Href = "/nachweis?tab=regeln",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Document templates — the library ones and, for HRB, the recruiting letters.</summary>
public sealed class DocumentTemplateSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(DocumentTemplate);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // one table serves the library and recruiting; the recruiting letters are HRB material
        var recruiting = RecruitingSeeder.TemplateCategory;
        var mayRecruiting = query.Scope.MayRecruiting;
        var q = db.DocumentTemplates.Where(t => mayRecruiting || t.Category != recruiting);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s)
                || (t.Description != null && t.Description.Contains(s))
                || t.ContentHtml.Contains(s));
        }
        var rows = await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new { t.Id, t.Name, t.Description, t.Category }).ToListAsync(cancellationToken);
        // the body is never the snippet: expanding a recruiting letter here would print the tokens the renderer
        // redacts, and the agent is meant to stay anonymous to applicants
        return rows.Select(t => new SearchHit(nameof(DocumentTemplate), t.Id, t.Name,
                string.Join(" · ", new[] { t.Category, t.Description }.Where(p => !string.IsNullOrWhiteSpace(p))),
                string.Empty)
            {
                Href = $"/admin/vorlagen/dokument-vorlage/{t.Id}/bearbeiten",
            })
            .ToList();
    }
}

/// <summary>Activity templates.</summary>
public sealed class ActivityTemplateSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(ActivityTemplate);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.ActivityTemplates.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s)
                || (t.Description != null && t.Description.Contains(s))
                || (t.ContentHtml != null && t.ContentHtml.Contains(s)));
        }
        var rows = await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new { t.Id, t.Name, t.Description, t.Kind }).ToListAsync(cancellationToken);
        return rows.Select(t => new SearchHit(nameof(ActivityTemplate), t.Id, t.Name,
                string.Join(" · ", new[] { t.Kind, t.Description }.Where(p => !string.IsNullOrWhiteSpace(p))),
                string.Empty)
            {
                Href = $"/admin/vorlagen/aktivitaet-vorlage/{t.Id}/bearbeiten",
            })
            .ToList();
    }
}

/// <summary>Personnel note templates. Leadership, like the files they are written into.</summary>
public sealed class PersonnelTemplateSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(PersonnelTemplate);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.PersonnelTemplates.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s)
                || (t.Description != null && t.Description.Contains(s))
                || (t.ContentHtml != null && t.ContentHtml.Contains(s)));
        }
        return await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(PersonnelTemplate), t.Id, t.Name, t.Description ?? string.Empty, string.Empty)
            {
                Href = $"/admin/vorlagen/personal-vorlage/{t.Id}/bearbeiten",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Person-dossier templates and their default field values.</summary>
public sealed class DocTemplateSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(DocTemplate);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.DocTemplates.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s)
                || (t.Description != null && t.Description.Contains(s))
                || (t.DefaultReason != null && t.DefaultReason.Contains(s))
                || (t.DefaultReceivedInformation != null && t.DefaultReceivedInformation.Contains(s)));
        }
        return await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(DocTemplate), t.Id, t.Name,
                t.Description ?? t.DefaultReason ?? string.Empty, string.Empty)
            {
                Href = "/einstellungen?tab=vorlagen-dok",
            })
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Cash-booking templates.</summary>
public sealed class KassenTemplateSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(KassenBuchungVorlage);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.KassenVorlagen.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(t => t.Name.Contains(s) || (t.Reason != null && t.Reason.Contains(s)));
        }
        return await q.OrderBy(t => t.Sorting).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(KassenBuchungVorlage), t.Id, t.Name, t.Reason ?? string.Empty, string.Empty)
            {
                Href = "/kasse?tab=vorlagen",
            })
            .ToListAsync(cancellationToken);
    }
}
