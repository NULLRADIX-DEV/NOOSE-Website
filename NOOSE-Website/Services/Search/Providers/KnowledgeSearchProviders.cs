using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Presentations;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services.Search.Providers;

/// <summary>Library documents: title, category and the rich-text body.</summary>
/// <remarks>The body is matched as stored HTML, so a phrase split by inline markup will not hit — the same
/// limitation the activity body has. The snippet is a window around the match, never the head of the document.</remarks>
public sealed class DocumentSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Document);

    public PartnerAccess Partner => PartnerAccess.ViaShare;

    public bool AppliesTo(SearchViewer viewer) => true;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(d => d.Title.Contains(s)
                || (d.Category != null && d.Category.Contains(s))
                || d.ContentHtml.Contains(s));
        }
        var rows = await q
            .OrderByDescending(d => d.Pinned)
            .ThenByDescending(d => d.ModifiedAt ?? d.CreatedAt)
            .Take(query.PerCategory)
            .Select(d => new { d.Id, d.Title, d.Category, d.ContentHtml })
            .ToListAsync(cancellationToken);

        return rows.Select(d => new SearchHit(nameof(Document), d.Id, d.Title,
                Snippet(d.Category, d.ContentHtml, query.Text), string.Empty))
            .ToList();
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        // identifiers only: the palette fires while the agent is still typing
        return await Visible(db, query).Where(d => d.Title.Contains(s))
            .OrderBy(d => d.Title).Take(max)
            .Select(d => new QuickHit(nameof(Document), d.Id, d.Title, d.Category ?? string.Empty))
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<Document> Visible(AppDbContext db, SearchQuery query)
    {
        var scope = query.Scope;
        return scope.PartnerAgency is { } agency
            ? db.Documents.OnlyPartnerVisible(db, agency, scope.MeId)
            : db.Documents.OnlyVisible(db, scope.AsDocumentScope());
    }

    // category first so a category match is visibly the reason for the hit, then the text around the match
    private static string Snippet(string? category, string html, string text)
    {
        var around = SearchSnippet.Around(HtmlCleanup.PlainText(html), text);
        return string.IsNullOrWhiteSpace(category) ? around : $"{category} · {around}";
    }
}

/// <summary>Meetings. The minutes are agenda-grade: they are only matched for meetings whose agenda is open.</summary>
public sealed class MeetingSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Meeting);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.Meetings.AsQueryable();
        if (query.HasText)
        {
            var s = query.Text;
            // the identifying fields are open to every internal agent; the minutes are not, and are filtered below
            q = q.Where(m => m.Title.Contains(s) || m.CaseNumber.Contains(s)
                || (m.Location != null && m.Location.Contains(s))
                || (m.MinutesHtml != null && m.MinutesHtml.Contains(s)));
        }
        var rows = await q.OrderByDescending(m => m.Start).Take(query.PerCategory)
            .Select(m => new { m.Id, m.Title, m.CaseNumber, m.Location, m.MinutesHtml, m.Start })
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return [];
        }

        // a match that exists only in the minutes must not surface before the agenda opens
        var open = await MeetingVisibility.OpenIdsAsync(
            db, rows.Select(m => m.Id).ToList(), query.Scope, query.Viewer.NowUtc, cancellationToken);
        var hits = new List<SearchHit>(rows.Count);
        foreach (var m in rows)
        {
            var identifying = !query.HasText
                || m.Title.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)
                || m.CaseNumber.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase)
                || (m.Location?.Contains(query.Text, StringComparison.CurrentCultureIgnoreCase) ?? false);
            var mayReadMinutes = open.Contains(m.Id);
            if (!identifying && !mayReadMinutes)
            {
                continue;
            }
            var snippet = mayReadMinutes
                ? SearchSnippet.Around(HtmlCleanup.PlainText(m.MinutesHtml), query.Text)
                : m.Location ?? string.Empty;
            hits.Add(new SearchHit(nameof(Meeting), m.Id, m.Title, snippet, m.CaseNumber) { Timestamp = m.Start });
        }
        return hits;
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        return await db.Meetings.Where(m => m.Title.Contains(s) || m.CaseNumber.Contains(s))
            .OrderByDescending(m => m.Start).Take(max)
            .Select(m => new QuickHit(nameof(Meeting), m.Id, m.Title, m.CaseNumber))
            .ToListAsync(cancellationToken);
    }
}

/// <summary>Agenda items and their notes. Both sit behind the meeting's time gate.</summary>
public sealed class MeetingAgendaItemSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(MeetingAgendaItem);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        if (!query.HasText)
        {
            return [];
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var raw = await db.MeetingAgendaItems
            .Where(p => p.Title.Contains(s) || (p.NotesHtml != null && p.NotesHtml.Contains(s)))
            .OrderBy(p => p.Sorting)
            .Select(p => new { p.Id, p.MeetingId, p.Title, p.NotesHtml })
            .Take(query.PerCategory * 4)
            .ToListAsync(cancellationToken);
        if (raw.Count == 0)
        {
            return [];
        }

        var meetingIds = raw.Select(p => p.MeetingId).Distinct().ToList();
        var open = await MeetingVisibility.OpenIdsAsync(db, meetingIds, query.Scope, query.Viewer.NowUtc, cancellationToken);
        if (open.Count == 0)
        {
            return [];
        }
        var meetings = (await db.Meetings.Where(m => open.Contains(m.Id))
                .Select(m => new { m.Id, m.Title, m.CaseNumber }).ToListAsync(cancellationToken))
            .ToDictionary(m => m.Id, StringComparer.Ordinal);

        var hits = new List<SearchHit>();
        foreach (var item in raw)
        {
            if (!meetings.TryGetValue(item.MeetingId, out var meeting))
            {
                continue;
            }
            var snippet = string.IsNullOrWhiteSpace(item.NotesHtml)
                ? item.Title
                : $"{item.Title} · {SearchSnippet.Around(HtmlCleanup.PlainText(item.NotesHtml), s)}";
            hits.Add(new SearchHit(nameof(MeetingAgendaItem), meeting.Id, meeting.Title, snippet,
                meeting.CaseNumber, nameof(Meeting)));
            if (hits.Count >= query.PerCategory)
            {
                break;
            }
        }
        return hits;
    }
}

/// <summary>Library files. Title and category only — the stored file itself is never read.</summary>
public sealed class LibraryFileSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(LibraryFile);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = db.LibraryFiles.OnlyVisible(query.Scope.AsDocumentScope());
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(f => f.Title.Contains(s)
                || (f.Category != null && f.Category.Contains(s))
                || f.OriginalName.Contains(s));
        }
        var rows = await q.OrderByDescending(f => f.ModifiedAt ?? f.CreatedAt).Take(query.PerCategory)
            .Select(f => new { f.Id, f.Title, f.Category, f.OriginalName }).ToListAsync(cancellationToken);
        return rows.Select(f => new SearchHit(nameof(LibraryFile), f.Id, f.Title,
                string.Join(" · ", new[] { f.Category, f.OriginalName }.Where(p => !string.IsNullOrWhiteSpace(p))),
                string.Empty)
            {
                // the library has no per-file page; the download endpoint is not a place to navigate to
                Href = "/dokumente",
            })
            .ToList();
    }
}

/// <summary>Handbook articles: what a page does and how a procedure runs.</summary>
/// <remarks>
/// Titles, summaries and slugs only, never the body. The category is <c>Quick</c>, and <c>Quick</c> excludes
/// <c>Heavy</c> — a longtext scan would push it into the second budget wave. The body is not lost: the assistant
/// reads it through <c>schlage_nach</c>, which is the tool for a question rather than a lookup.
/// <para>
/// The slug is the key everywhere in the search path - hit target, index entry and id resolution - because
/// <c>/handbuch/{Slug}</c> is the article address and the side-index pass dedupes on the target id.
/// </para>
/// </remarks>
public sealed class HandbookArticleSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(HandbookArticle);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(a => a.Title.Contains(s) || a.Slug.Contains(s)
                || (a.Summary != null && a.Summary.Contains(s)));
        }
        return await q.OrderBy(a => a.SortOrder).ThenBy(a => a.Title).Take(query.PerCategory)
            .Select(a => new SearchHit(nameof(HandbookArticle), a.Slug, a.Title, a.Summary ?? string.Empty, string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // by slug, the same key the index and the hit carry - see SearchIndexProjection
        return await Visible(db).Where(a => ids.Contains(a.Slug)).Take(take)
            .Select(a => new SearchHit(nameof(HandbookArticle), a.Slug, a.Title, a.Summary ?? string.Empty, string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(
        SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        return await Visible(db).Where(a => a.Title.Contains(s))
            .OrderBy(a => a.SortOrder).ThenBy(a => a.Title).Take(max)
            .Select(a => new QuickHit(nameof(HandbookArticle), a.Slug, a.Title, string.Empty))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The one gate, shared by recall, palette and side-index resolution.</summary>
    /// <remarks>Mirrors <c>HandbookService</c>: an article in a hidden chapter is hidden with it. Soft delete is
    /// the global filter and needs no clause here.</remarks>
    private static IQueryable<HandbookArticle> Visible(AppDbContext db)
        => db.HandbuchArtikel
            .Where(a => a.IsVisible
                && db.HandbuchKapitel.Any(c => c.Id == a.ChapterId && c.IsVisible));
}

/// <summary>Glossary terms: the in-house vocabulary, one sentence each.</summary>
/// <remarks>
/// The glossary has no page of its own - it is a section of <c>/handbuch</c> - so the hit opens the handbook with
/// the term named in a query parameter. By id, not by name: the route template is formatted and never URL-encoded,
/// and a term carries spaces and umlauts.
/// <para>No soft delete on this table; a term is withdrawn with <c>IsVisible</c>.</para>
/// </remarks>
public sealed class GlossaryTermSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(GlossaryTerm);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db);
        if (query.HasText)
        {
            var s = query.Text;
            // synonyms are searched too: somebody looking for "Deckname" means "Codename"
            q = q.Where(t => t.Term.Contains(s) || t.ShortDefinition.Contains(s)
                || (t.Synonyms != null && t.Synonyms.Contains(s)));
        }
        return await q.OrderBy(t => t.Term).Take(query.PerCategory)
            .Select(t => new SearchHit(nameof(GlossaryTerm), t.Id, t.Term, t.ShortDefinition, string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db).Where(t => ids.Contains(t.Id)).Take(take)
            .Select(t => new SearchHit(nameof(GlossaryTerm), t.Id, t.Term, t.ShortDefinition, string.Empty))
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(
        SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        return await Visible(db).Where(t => t.Term.Contains(s) || (t.Synonyms != null && t.Synonyms.Contains(s)))
            .OrderBy(t => t.Term).Take(max)
            .Select(t => new QuickHit(nameof(GlossaryTerm), t.Id, t.Term, string.Empty))
            .ToListAsync(cancellationToken);
    }

    private static IQueryable<GlossaryTerm> Visible(AppDbContext db)
        => db.HandbuchBegriffe.Where(t => t.IsVisible);
}

/// <summary>Radio channels: the reverse lookup for a frequency somebody overheard.</summary>
/// <remarks>
/// The search text is normalised before it is matched against the frequency, because a German keyboard types
/// "411,7" where the plan stores "411.7" — without that the one query this category exists for finds nothing.
/// The label is matched unnormalised, so a comma inside a name still works.
/// <para>Visibility is <see cref="RadioVisibility.OnlyVisible"/>, the same expression the plan page reads.</para>
/// </remarks>
public sealed class RadioChannelSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(RadioChannel);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => !viewer.IsPartner;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = Visible(db, query);
        if (query.HasText)
        {
            var s = query.Text;
            var frequency = RadioFrequency.Normalize(s);
            q = q.Where(c => c.Frequency.Contains(frequency) || c.Label.Contains(s)
                || (c.Note != null && c.Note.Contains(s)));
        }
        return await Ordered(q).Take(query.PerCategory).Select(Hit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SearchHit>> ResolveIdsAsync(
        SearchQuery query, IReadOnlyCollection<string> ids, int take, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await Visible(db, query).Where(c => ids.Contains(c.Id)).Take(take)
            .Select(Hit).ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(
        SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        var frequency = RadioFrequency.Normalize(s);
        return await Ordered(Visible(db, query).Where(c => c.Frequency.Contains(frequency) || c.Label.Contains(s)))
            .Take(max)
            .Select(c => new QuickHit(nameof(RadioChannel), c.Id, c.Frequency + " · " + c.Label, string.Empty))
            .ToListAsync(cancellationToken);
    }

    // the frequency is the answer to the question this category is asked, so it leads the row
    private static System.Linq.Expressions.Expression<Func<RadioChannel, SearchHit>> Hit =>
        c => new SearchHit(nameof(RadioChannel), c.Id, c.Frequency, c.Label, string.Empty);

    private static IQueryable<RadioChannel> Ordered(IQueryable<RadioChannel> query)
        => query.OrderBy(c => c.Scope).ThenBy(c => c.Frequency);

    private static IQueryable<RadioChannel> Visible(AppDbContext db, SearchQuery query)
        => db.Funkkanaele.OnlyVisible(db, query.Scope);
}

/// <summary>Presentations: title, description and slide titles, never the slide bodies or the speaker notes.</summary>
/// <remarks>
/// The palette offers them by title; the @-picker and the compromise picker drop them again through
/// <see cref="LinkService.ReferenceTypes"/>, because their resolver knows neither groups nor invites.
/// <para>Visibility is <see cref="PresentationVisibility.OnlyVisible"/>, the same expression the list reads.</para>
/// </remarks>
public sealed class PresentationSearchProvider(IDbContextFactory<AppDbContext> dbFactory) : ISearchProvider
{
    public string Category => nameof(Presentation);

    public PartnerAccess Partner => PartnerAccess.Never;

    public bool AppliesTo(SearchViewer viewer) => viewer.Scope.IsInternalAgent;

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var q = PresentationVisibility.OnlyVisible(db.Presentations, db, query.Scope);
        if (query.HasText)
        {
            var s = query.Text;
            q = q.Where(p => p.Title.Contains(s)
                || (p.Description != null && p.Description.Contains(s))
                || db.PresentationSlides.Any(f => f.PresentationId == p.Id && f.Title != null && f.Title.Contains(s)));
        }
        var rows = await q.OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt).Take(query.PerCategory)
            .Select(p => new { p.Id, p.Title, p.Description, Updated = p.ModifiedAt ?? p.CreatedAt })
            .ToListAsync(cancellationToken);
        return rows.Select(p => new SearchHit(nameof(Presentation), p.Id, p.Title, p.Description ?? string.Empty, string.Empty)
            {
                Timestamp = p.Updated,
            })
            .ToList();
    }

    public async Task<IReadOnlyList<QuickHit>> QuickAsync(SearchQuery query, int max, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var s = query.Text;
        // title only: the palette fires while the agent is still typing
        return await PresentationVisibility.OnlyVisible(db.Presentations, db, query.Scope)
            .Where(p => p.Title.Contains(s))
            .OrderByDescending(p => p.ModifiedAt ?? p.CreatedAt).Take(max)
            .Select(p => new QuickHit(nameof(Presentation), p.Id, p.Title, string.Empty))
            .ToListAsync(cancellationToken);
    }
}
