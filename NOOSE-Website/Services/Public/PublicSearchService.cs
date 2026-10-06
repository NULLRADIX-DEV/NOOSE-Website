using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc cref="IPublicSearchService" />
public class PublicSearchService(
    IPublicModuleService modules,
    IPublicWantedService wanted,
    IPublicFactionProfileService factions,
    IPressReleaseService press,
    IPublicWarningService warnings,
    IPublicReportService reports,
    IPublicPageService pages,
    IPublicFaqService faq,
    IPublicLawService laws) : IPublicSearchService
{
    /// <summary>One candidate before matching: what is shown, and the text that decides whether it matches.</summary>
    private readonly record struct Candidate(
        string Title, string? Reference, string? Href, DateTime? PublishedAt, string Haystack);

    public async Task<PublicSearchResults> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        // its own switch first: a read path answers an off module with nothing, it never throws
        if (!await modules.IsEnabledAsync(PublicModules.PublicSearch, cancellationToken))
        {
            return PublicSearchResults.Empty;
        }

        var text = PublicSearchRules.Normalise(query);
        if (PublicSearchRules.IsTooShort(text))
        {
            return PublicSearchResults.Empty;
        }

        var words = TextSimilarity.Tokens(text);
        var groups = new List<PublicSearchGroup>();
        foreach (var area in PublicSearchAreaDisplay.All)
        {
            // one area at a time, never Task.WhenAll: the sources are independent snapshots, but the group order is
            // part of the page and a failure has to stay local to the surface that produced it
            IReadOnlyList<Candidate> candidates;
            try
            {
                candidates = await CandidatesAsync(area, cancellationToken);
            }
            catch (Exception)
            {
                /* best effort */
                continue;
            }

            var matched = Match(candidates, text, words);
            if (matched.Count == 0)
            {
                continue;
            }
            var capped = matched.Count > PublicSearchRules.PerAreaLimit;
            groups.Add(new PublicSearchGroup(area,
                matched.Take(PublicSearchRules.PerAreaLimit)
                    .Select(c => new PublicSearchHit(area, c.Title, Snippet(c.Haystack, text), c.Href, c.Reference,
                        c.PublishedAt))
                    .ToList(),
                capped));
        }

        return new PublicSearchResults(text, groups);
    }

    /// <summary>Substring hits first, then the typo-tolerant ones, newest first within each.</summary>
    /// <remarks>
    /// The asymmetric phrase measure is the right one here: it wants a partner for every word the visitor typed,
    /// which is what a search box means. The symmetric twin exists for duplicate detection, a different question.
    /// </remarks>
    private static List<Candidate> Match(IReadOnlyList<Candidate> candidates, string text, IReadOnlyList<string> words)
    {
        var exact = new List<Candidate>();
        var fuzzy = new List<(Candidate Candidate, int Distance)>();
        foreach (var candidate in candidates)
        {
            if (candidate.Haystack.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            {
                exact.Add(candidate);
            }
            else if (words.Count > 0
                && TextSimilarity.PhraseSimilar(words, TextSimilarity.Tokens(candidate.Haystack), out var distance))
            {
                fuzzy.Add((candidate, distance));
            }
        }

        return
        [
            .. exact.OrderByDescending(c => c.PublishedAt ?? DateTime.MinValue),
            .. fuzzy.OrderBy(f => f.Distance).ThenByDescending(f => f.Candidate.PublishedAt ?? DateTime.MinValue)
                .Select(f => f.Candidate),
        ];
    }

    private static string Snippet(string haystack, string text)
    {
        var plain = haystack.Trim();
        var at = plain.IndexOf(text, StringComparison.CurrentCultureIgnoreCase);
        if (at < 0)
        {
            return plain.Length <= PublicSearchRules.SnippetRadius * 2
                ? plain
                : plain[..Back(plain, PublicSearchRules.SnippetRadius * 2)].TrimEnd() + "…";
        }
        var from = Back(plain, Math.Max(0, at - PublicSearchRules.SnippetRadius));
        var to = Back(plain, Math.Min(plain.Length, at + text.Length + PublicSearchRules.SnippetRadius));
        return (from > 0 ? "…" : string.Empty) + plain[from..to].Trim() + (to < plain.Length ? "…" : string.Empty);
    }

    /// <summary>
    /// The published rows of one surface, read through the service that owns them.
    /// </summary>
    /// <remarks>
    /// Not searched, each with its reason: the capture archive (a hit would link nowhere, and a name-searchable
    /// archive is a permanent "was this person ever caught" lookup the address-less list deliberately is not) · the
    /// two hazard lists (rankings over rows this already returns, so indexing them lists the same subject twice and
    /// makes the hazard level look like a searchable attribute) · the threat level and the figures (one editorial
    /// sentence and a band of numbers, neither of them a search subject) · bounty amounts (an amount is not a search
    /// term, and a searchable one is a price list of heads) · everything in <c>PublicVisibility.NeverPublic</c>.
    /// </remarks>
    private async Task<IReadOnlyList<Candidate>> CandidatesAsync(PublicSearchArea area, CancellationToken ct)
        => area switch
        {
            PublicSearchArea.Fahndung => await NoticesAsync(ct),
            PublicSearchArea.Organisationen => await OrganisationsAsync(ct),
            PublicSearchArea.Presse => await PressAsync(ct),
            PublicSearchArea.Warnungen => await WarningsAsync(ct),
            PublicSearchArea.Berichte => await ReportsAsync(ct),
            PublicSearchArea.Information => await PagesAsync(ct),
            PublicSearchArea.Fragen => await FaqAsync(ct),
            PublicSearchArea.Recht => await LawsAsync(ct),
            // an area without an arm produces an empty group, never another surface's rows: the discard arm that
            // stood here answered "Recht" for anything new, which would have shipped every paragraph twice
            _ => [],
        };

    private async Task<IReadOnlyList<Candidate>> NoticesAsync(CancellationToken ct)
    {
        var board = await wanted.GetBoardAsync(ct);
        // the haystack is precomputed in the snapshot: stripping every accusation's markup here would redo that
        // work, base64 pictures included, on every anonymous request
        return board.Cards
            .Select(card =>
            {
                // the notice haystack spans five fields, so its fallback has to rebuild all of them - Body() alone
                // would leave the alias, the area and the plate out of the search
                var precomputed = board.SearchTextFor(card.CaseNumber);
                var detail = board.Find(card.CaseNumber);
                var haystack = precomputed.Length > 0
                    ? precomputed
                    : Join(card.DisplayName, card.AliasText, detail?.LastArea, detail?.VehicleText,
                        HtmlCleanup.PlainText(detail?.ChargeHtml));
                return new Candidate(card.DisplayName, card.CaseNumber, $"/gesucht/{card.CaseNumber}",
                    card.PublishedAt, haystack);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> OrganisationsAsync(CancellationToken ct)
    {
        var board = await factions.GetBoardAsync(ct);
        return board.Cards
            .Select((card, i) => new Candidate(card.DisplayName, null, "/organisationen", card.PublishedAt,
                Join(card.DisplayName, Body(board.SearchTextAt(i), card.DescriptionHtml))))
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> PressAsync(CancellationToken ct)
    {
        var snapshot = await press.GetPublishedAsync(ct);
        return snapshot.Cards
            .Select(card => new Candidate(card.Title, card.CaseNumber, $"/presse/{card.CaseNumber}", card.PublishedAt,
                Join(card.Title, card.Teaser,
                    Body(snapshot.SearchTextFor(card.CaseNumber), snapshot.Find(card.CaseNumber)?.Html))))
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> WarningsAsync(CancellationToken ct)
    {
        var snapshot = await warnings.GetPublishedAsync(ct);
        // the hub is the warning's address: there is no detail route, by design
        return snapshot.Cards
            .Select((card, i) => new Candidate(card.Title, null, "/warnungen", card.PublishedAt,
                Join(card.Title, Body(snapshot.SearchTextAt(i), card.Html))))
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> ReportsAsync(CancellationToken ct)
    {
        var snapshot = await reports.GetPublishedAsync(ct);
        return snapshot.Cards
            .Select(card =>
            {
                var period = ReportPeriod.Format(card.Year, card.Month);
                return new Candidate(card.Title, ReportPeriod.Label(card.Year, card.Month), $"/berichte/{period}",
                    card.PublishedAt,
                    Join(card.Title, Body(snapshot.SearchTextFor(period), snapshot.Find(period)?.Html)));
            })
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> PagesAsync(CancellationToken ct)
    {
        var snapshot = await pages.GetPublishedAsync(ct);
        // only the linked pages. Status decides whether a page is public, ShowInMenu only whether it is linked, and a
        // published page kept out of the menu is "reachable by direct link" on purpose — search is a second menu
        var linked = snapshot.Menu.Select(m => m.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return snapshot.Pages.Values
            .Where(page => linked.Contains(page.Slug))
            .Select(page => new Candidate(page.Title, null, $"/info/{page.Slug}", page.PublishedAt,
                Join(page.Title, Body(snapshot.SearchTextFor(page.Slug), page.Html))))
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> FaqAsync(CancellationToken ct)
    {
        // one hit per question, addressed by its own anchor. The query part is what actually opens it: the page
        // renders statically, so a fragment alone would arrive at a closed section. Both gates - the module and
        // the FAQ page being published - are already applied inside the snapshot.
        var snapshot = await faq.GetPublishedAsync(ct);
        return snapshot.All()
            .Select(pair => new Candidate(pair.Entry.Question, pair.Rubrik.Title, PublicFaq.Href(pair.Entry.Anchor),
                // no date: a question is not a dated statement, and PublishedAt only decides the tie-break, where
                // null leaves the editorial order the sections already define
                null,
                Join(pair.Rubrik.Title, pair.Entry.Question, pair.Entry.PlainText)))
            .ToList();
    }

    private async Task<IReadOnlyList<Candidate>> LawsAsync(CancellationToken ct)
    {
        var snapshot = await laws.GetPublishedAsync(ct);
        return snapshot.Books
            .SelectMany(book => book.Entries.Select(entry => new Candidate(
                $"{entry.Paragraph} {entry.Title}".Trim(), $"{book.Name} {entry.Paragraph}".Trim(), "/recht", null,
                Join(book.Name, book.Title, entry.Paragraph, entry.Title, Body(entry.PlainText, entry.Text), entry.Sentence))))
            .ToList();
    }

    private static string Join(params string?[] parts)
        => string.Join(" · ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

    /// <summary>Moves a cut off the middle of a surrogate pair, always backwards.</summary>
    /// <remarks>
    /// The cuts are UTF-16 indices and the haystack comes from the database, so slicing between the halves of a
    /// pair would emit a replacement character on an anonymous page. Backwards, so the snippet never grows.
    /// </remarks>
    private static int Back(string text, int index)
        => index > 0 && index < text.Length && char.IsLowSurrogate(text[index]) ? index - 1 : index;

    /// <summary>The precomputed plain text of a body, or the markup stripped here when a snapshot carries none.</summary>
    /// <remarks>
    /// The precomputation is a saving, never the source of truth: a snapshot built without it - a test double, or a
    /// future producer that forgets the field - must still be searchable rather than silently lose its bodies.
    /// </remarks>
    private static string Body(string precomputed, string? html)
        => precomputed.Length > 0 ? precomputed : HtmlCleanup.PlainText(html);
}
