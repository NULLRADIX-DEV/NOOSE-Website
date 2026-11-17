using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Public notices. A publication snapshot of a person file, so a hit targets the file it came from.</summary>
/// <remarks>
/// The join against <see cref="RecordVisibility.OnlyVisible{T}"/> is the visibility predicate and the suppression
/// belt in one — the provider names the rule instead of restating it. Deliberately narrower than the desk in one
/// respect: <c>GetAllAsync</c> resolves secrecy over <c>IgnoreQueryFilters</c> so a notice on a deleted file stays
/// manageable, while a hit has to be navigable and a deleted file has no page to land on.
/// </remarks>
public sealed class PublicWantedNoticeSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(OeffentlicheFahndung);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequirePublicWantedRead: a search is a cross-list, and the wider per-record guard
    // exists only so a rank 1-2 agent can open the one notice they are preparing
    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent()
           && (viewer.User.MayHighestClassification() || viewer.User.IsOnlyReader());

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        // explicit join rather than Include: the required navigation carries the soft-delete filter
        var raw = await (
            from f in db.OeffentlicheFahndungen.AsNoTracking()
            where f.DisplayName.Contains(s)
                || (f.CaseNumber != null && f.CaseNumber.Contains(s))
                || (f.AliasText != null && f.AliasText.Contains(s))
                || (f.LastArea != null && f.LastArea.Contains(s))
                || (f.VehicleText != null && f.VehicleText.Contains(s))
                || (f.ChargeHtml != null && f.ChargeHtml.Contains(s))
            join p in db.People.OnlyVisible(query.Scope) on f.PersonId equals p.Id
            orderby f.PublishedAt descending
            select new
            {
                PersonId = p.Id,
                PersonName = p.Name,
                PersonCase = p.CaseNumber,
                f.Kind,
                f.Status,
                f.DisplayName,
            })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        // the charge matches but is never projected: it is published rich text and may carry base64 images
        return raw
            .Select(r => new SearchHit(nameof(OeffentlicheFahndung), r.PersonId, r.PersonName,
                $"{PublicWantedKindDisplay.Name(r.Kind)} · {r.DisplayName} · {PublicWantedStatusDisplay.Name(r.Status)}",
                r.PersonCase, nameof(Person)))
            .ToList();
    }
}

/// <summary>Public organisation profiles. A publication snapshot of a faction file, found through that file.</summary>
public sealed class PublicFactionProfileSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(OeffentlichesFraktionsprofil);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequirePublicFactionProfileRead, for the same reason as the notice list
    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent()
           && (viewer.User.MayHighestClassification() || viewer.User.IsOnlyReader());

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await (
            from p in db.OeffentlicheFraktionsprofile.AsNoTracking()
            where p.DisplayName.Contains(s) || (p.DescriptionHtml != null && p.DescriptionHtml.Contains(s))
            join f in db.Factions.OnlyVisible(query.Scope) on p.FactionId equals f.Id
            orderby p.PublishedAt descending
            select new
            {
                FactionId = f.Id,
                FactionName = f.Name,
                FactionCase = f.CaseNumber,
                p.DisplayName,
                p.Standing,
                p.Status,
            })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        // the description matches but is never projected, for the same reason as the charge
        return raw
            .Select(r => new SearchHit(nameof(OeffentlichesFraktionsprofil), r.FactionId, r.FactionName,
                $"{r.DisplayName} · {PublicFactionStandingDisplay.Name(r.Standing)} · {PublicProfileStatusDisplay.Name(r.Status)}",
                r.FactionCase, nameof(Faction)))
            .ToList();
    }
}

/// <summary>Citizen tips. Every internal agent, the read-only supervision included.</summary>
/// <remarks>
/// Carries no citizen field at all, unconditionally — not even where the anonymity promise no longer holds. Matching
/// a name would turn the global search into an anonymity oracle by trial, and the audited leadership resolution is
/// meant to be the only way to a name. It also sidesteps the required navigation to a soft-deletable profile, which
/// would INNER-join the tips of a removed citizen out of the result without saying so.
/// </remarks>
public sealed class TipSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Hinweis);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequireTipRead
    public bool AppliesTo(SearchViewer viewer) => viewer.User.IsInternalAgent();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.Hinweise.AsNoTracking()
            .Where(h => h.CaseNumber.Contains(s) || h.Text.Contains(s))
            .OrderByDescending(h => h.CreatedAt)
            .Select(h => new { h.Id, h.CaseNumber, h.Text, h.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        // title wording verbatim as in RecordsReference and LinkService: one tip reads the same everywhere
        return raw
            .Select(h => new SearchHit(nameof(Hinweis), h.Id, "Bürgerhinweis " + h.CaseNumber,
                SearchSnippet.Around(h.Text, query.Text), h.CaseNumber)
            {
                Timestamp = h.CreatedAt,
            })
            .ToList();
    }
}

/// <summary>Citizen tickets to the leadership desk.</summary>
/// <remarks>
/// Subject and case number only. The desk filter box also matches the citizen's name, but that runs on an
/// already-authorised list; here it would be a name oracle, and dereferencing the required profile navigation would
/// drop the tickets of a removed citizen silently.
/// </remarks>
public sealed class TicketSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Ticket);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequireTicketRead
    public bool AppliesTo(SearchViewer viewer) => viewer.User.MayTicketRead();

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.Tickets.AsNoTracking()
            .Where(t => t.CaseNumber.Contains(s) || t.Subject.Contains(s))
            .OrderByDescending(t => t.LastActivityAt)
            .Select(t => new { t.Id, t.CaseNumber, t.Subject, t.Status, t.LastActivityAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            .Select(t => new SearchHit(nameof(Ticket), t.Id, t.Subject,
                TicketStatusDisplay.Name(t.Status), t.CaseNumber)
            {
                Timestamp = t.LastActivityAt,
            })
            .ToList();
    }
}

/// <summary>Citizen objections against a public notice. Decided at a section of the wanted desk.</summary>
/// <remarks>
/// Rooted the way the desk roots it — over the soft-delete filter with <c>!IsDeleted</c> written back by hand — so
/// the two answer the same set. The desk needs the widening because it dereferences the required notice navigation,
/// which EF joins INNER; this provider does not, and rooting it identically is what keeps a later projection safe.
/// </remarks>
public sealed class ObjectionSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(FahndungEinspruch);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequireObjectionRead: an objection is wanted-desk work, not leadership correspondence
    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent()
           && (viewer.User.MayHighestClassification() || viewer.User.IsOnlyReader());

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.FahndungEinsprueche.IgnoreQueryFilters().AsNoTracking()
            .Where(e => !e.IsDeleted)
            .Where(e => e.CaseNumber.Contains(s) || e.Text.Contains(s))
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new { e.Id, e.CaseNumber, e.Text, e.Status, e.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            // the status is named in the title, like the eight sibling providers do: the desk tab this links to
            // shows OPEN objections, so a decided one used to look like a wrong hit
            .Select(e => new SearchHit(nameof(FahndungEinspruch), e.Id,
                $"Einspruch {e.CaseNumber} ({ObjectionStatusDisplay.Name(e.Status)})",
                SearchSnippet.Around(e.Text, query.Text), e.CaseNumber)
            {
                Timestamp = e.CreatedAt,
                Href = "/fahndung?tab=einsprueche",
            })
            .ToList();
    }
}

/// <summary>Press releases, drafts included — an internal search over the agency's own voice.</summary>
public sealed class PressReleaseSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Pressemitteilung);

    public PartnerAccess Partner => PartnerAccess.Never;

    // the audience of Permission.RequireClassifiedRead, which the press service holds on every read path
    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent() && viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        // both bodies match, neither is projected: they carry base64 images, and fifty of them is megabytes a render
        var raw = await db.Pressemitteilungen.AsNoTracking()
            .Where(m => m.Title.Contains(s) || m.Teaser.Contains(s)
                || (m.CaseNumber != null && m.CaseNumber.Contains(s))
                || (m.DraftHtml != null && m.DraftHtml.Contains(s))
                || (m.ContentHtml != null && m.ContentHtml.Contains(s)))
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Id, m.CaseNumber, m.Title, m.Teaser, m.Status, m.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            .Select(m => new SearchHit(nameof(Pressemitteilung), m.Id, m.Title,
                $"{PressReleaseStatusDisplay.Name(m.Status)} · {m.Teaser}", m.CaseNumber ?? string.Empty)
            {
                Timestamp = m.CreatedAt,
                Href = "/einstellungen?tab=presse",
            })
            .ToList();
    }
}

/// <summary>Editorial pages of the public area, drafts included.</summary>
public sealed class PublicPageSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(OeffentlicheSeite);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent() && viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.OeffentlicheSeiten.AsNoTracking()
            .Where(p => p.Title.Contains(s) || p.Slug.Contains(s)
                || (p.MenuTitle != null && p.MenuTitle.Contains(s))
                || (p.DraftHtml != null && p.DraftHtml.Contains(s))
                || (p.ContentHtml != null && p.ContentHtml.Contains(s)))
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Title)
            .Select(p => new { p.Id, p.Slug, p.Title, p.Status, p.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            .Select(p => new SearchHit(nameof(OeffentlicheSeite), p.Id, p.Title,
                $"{PublicPageStatusDisplay.Name(p.Status)} · /info/{p.Slug}", string.Empty)
            {
                Timestamp = p.CreatedAt,
                Href = "/einstellungen?tab=oeffentliche-seiten",
            })
            .ToList();
    }
}

/// <summary>Official public warnings, drafts included.</summary>
public sealed class PublicWarningSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(OeffentlicheWarnung);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent() && viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.OeffentlicheWarnungen.AsNoTracking()
            .Where(w => w.Title.Contains(s)
                || (w.ContentTitle != null && w.ContentTitle.Contains(s))
                || (w.DraftHtml != null && w.DraftHtml.Contains(s))
                || (w.ContentHtml != null && w.ContentHtml.Contains(s)))
            .OrderByDescending(w => w.CreatedAt)
            .Select(w => new { w.Id, w.Title, w.Status, w.ValidUntil, w.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            .Select(w => new SearchHit(nameof(OeffentlicheWarnung), w.Id, w.Title,
                w.ValidUntil is { } until
                    ? $"{PublicWarningStatusDisplay.Name(w.Status)} · gültig bis {until.ToLocalTime():dd.MM.yyyy}"
                    : PublicWarningStatusDisplay.Name(w.Status),
                string.Empty)
            {
                Timestamp = w.CreatedAt,
                Href = "/einstellungen?tab=warnungen",
            })
            .ToList();
    }
}

/// <summary>Released monthly texts, drafts included. Not the internal report they were written from.</summary>
public sealed class PublicReportSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(OeffentlicherLagebericht);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer)
        => viewer.User.IsInternalAgent() && viewer.Scope.MayClassifiedRead;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;

        var raw = await db.OeffentlicheLageberichte.AsNoTracking()
            .Where(r => r.Title.Contains(s)
                || (r.ContentTitle != null && r.ContentTitle.Contains(s))
                || (r.DraftHtml != null && r.DraftHtml.Contains(s))
                || (r.ContentHtml != null && r.ContentHtml.Contains(s)))
            .OrderByDescending(r => r.Year).ThenByDescending(r => r.Month)
            .Select(r => new { r.Id, r.Title, r.Year, r.Month, r.Status, r.CreatedAt })
            .Take(query.PerCategory)
            .ToListAsync(cancellationToken);

        return raw
            .Select(r => new SearchHit(nameof(OeffentlicherLagebericht), r.Id, r.Title,
                $"{ReportPeriod.Label(r.Year, r.Month)} · {PublicReportStatusDisplay.Name(r.Status)}",
                string.Empty)
            {
                Timestamp = r.CreatedAt,
                Href = "/einstellungen?tab=berichte",
            })
            .ToList();
    }
}
