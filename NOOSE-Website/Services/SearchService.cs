using System.Diagnostics;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ISearchService" />
/// <remarks>
/// An orchestrator, not a query. Every category lives in its own <see cref="ISearchProvider"/>; this decides which
/// of them the viewer may use, runs them under a wall-clock budget, ranks what came back and reports what did not.
/// </remarks>
public class SearchService(
    IEnumerable<ISearchProvider> providers,
    IDbContextFactory<AppDbContext> dbFactory,
    IPartnerVisibilityPolicyService partnerPolicy,
    IOptions<SearchOptions> options,
    ILogger<SearchService> logger) : ISearchService
{
    private readonly SearchOptions _options = options.Value;

    // catalog order, not registration order: the facet bar, the result list and a saved facet read one sequence
    private readonly IReadOnlyList<ISearchProvider> _providers = providers
        .OrderBy(p => SearchCatalog.Index(p.Category))
        .ToList();

    public async Task<SearchResults> SearchAsync(
        SearchCriteria criteria, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var viewer = await SearchViewer.FromAsync(actor, partnerPolicy, cancellationToken);
        var query = SearchQuery.From(criteria, viewer, _options);

        var allowed = Allowed(viewer).ToList();
        var runnable = allowed.Where(p => Selected(query, p) && Answers(query, p)).ToList();
        if (runnable.Count == 0)
        {
            return SearchResults.None with { VisibleCategories = allowed.Count, Elapsed = started.Elapsed };
        }

        // one budget for the whole search; a per-provider ceiling sits inside it
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(1_000, _options.BudgetMs)));

        var found = new IReadOnlyList<SearchHit>?[runnable.Count];
        // cheap categories first, longtext scans second: when the clock runs out the gap must be the heavy wave,
        // never the record types the agent was actually looking for
        await WaveAsync(runnable, found, query, heavy: false, budget.Token);
        await WaveAsync(runnable, found, query, heavy: true, budget.Token);

        // a viewer who navigated away gets an exception, not a bogus partial — same rule as the assistant's turn
        cancellationToken.ThrowIfCancellationRequested();

        var groups = new List<SearchResultGroup>(runnable.Count);
        var searched = new List<string>(runnable.Count);
        var incomplete = new List<string>();
        for (var index = 0; index < runnable.Count; index++)
        {
            var category = runnable[index].Category;
            if (found[index] is not { } hits)
            {
                incomplete.Add(category);
                continue;
            }
            searched.Add(category);
            if (hits.Count == 0)
            {
                continue;
            }
            // recall is LIKE; ordering happens here, once, for every category alike
            var ranked = query.HasText ? SearchRelevance.Rank(query.Text, hits.ToList()) : hits.ToList();
            groups.Add(new SearchResultGroup(category, SearchCatalog.Plural(category), ranked,
                Capped: ranked.Count >= query.PerCategory));
        }

        // phonetic/stem recall last: weakest matches, appended after ranking, resolved through the providers so
        // each visibility rule stays with the one place that owns it
        var sideIndexSkipped = false;
        if (query.Fuzzy && query.HasText)
        {
            try
            {
                budget.Token.ThrowIfCancellationRequested();
                await SearchSideIndex.AppendAsync(dbFactory, runnable, groups, query, budget.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                /* budget spent; the groups collected so far still stand */
                sideIndexSkipped = true;
            }
        }

        return new SearchResults(groups, groups.Sum(g => g.Hit.Count), searched, incomplete,
            allowed.Count, started.Elapsed) { SideIndexSkipped = sideIndexSkipped };
    }

    public async Task<List<QuickHit>> QuickSearchAsync(
        string text, ClaimsPrincipal actor, int max = 8, CancellationToken cancellationToken = default)
    {
        var trimmed = text?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
        {
            return [];
        }
        var viewer = await SearchViewer.FromAsync(actor, partnerPolicy, cancellationToken);
        var query = SearchQuery.From(new SearchCriteria { Text = trimmed, Fuzzy = true }, viewer, _options)
            // a much smaller candidate pool than the page uses: see SearchOptions.QuickFuzzyCandidates
            with { FuzzyCandidates = Math.Max(50, _options.QuickFuzzyCandidates) };
        var runnable = Allowed(viewer)
            .Where(p => SearchCatalog.Has(p.Category, SearchTraits.Quick))
            .ToList();
        if (runnable.Count == 0)
        {
            return [];
        }

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(100, _options.QuickBudgetMs)));

        var lists = new List<IReadOnlyList<QuickHit>>(runnable.Count);
        foreach (var provider in runnable)
        {
            try
            {
                var hits = await provider.QuickAsync(query, max, budget.Token);
                if (hits.Count > 0)
                {
                    lists.Add(SearchRelevance.RankQuick(trimmed, hits.ToList()));
                }
            }
            // budget spent: hand back what is already there. The viewer's own cancel rethrows.
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Schnellsuche: Kategorie {Category} fehlgeschlagen.", provider.Category);
            }
        }
        // round-robin, so one populous category cannot fill every slot
        return SearchProviderKit.Shuffle(lists).Take(max).ToList();
    }

    /// <summary>Providers this viewer may use at all. Two independent caps for a partner, both re-asserted here.</summary>
    private IEnumerable<ISearchProvider> Allowed(SearchViewer viewer)
    {
        if (!viewer.IsPartner)
        {
            return _providers.Where(p => p.AppliesTo(viewer));
        }
        return _providers
            .Where(p => p.Partner != PartnerAccess.Never)
            // cap 1: the hard nine-type ceiling, asserted rather than trusted. A child provider is exempt by
            // construction — its parent check rejects a non-releasable parent type before anything else.
            .Where(p => p.Partner == PartnerAccess.ViaParentShare || PartnerVisibility.IsReleasableType(p.Category))
            // cap 2: the rank allowlist. It used to gate navigation only, so a partner could find by search what
            // their rank was never allowed to list.
            .Where(p => viewer.PartnerAllowedTypes is null
                || p.Partner == PartnerAccess.ViaParentShare
                || viewer.PartnerAllowedTypes.Contains(p.Category))
            .Where(p => p.AppliesTo(viewer));
    }

    /// <summary>Whether the caller restricted the search to this category. Empty = everything the viewer may use.</summary>
    /// <remarks>The list is attacker-controlled — it round-trips through a saved search and the query string — so
    /// it only ever narrows what <see cref="Allowed"/> already permitted, and an unknown key is dropped silently.</remarks>
    private static bool Selected(SearchQuery query, ISearchProvider provider)
        => query.OnlyCategories.Count == 0
        || query.OnlyCategories.Contains(provider.Category, StringComparer.Ordinal);

    /// <summary>Whether this category can answer this query at all.</summary>
    /// <remarks>A tag-scoped query skips categories that carry no tags: they would answer zero, and zero reads
    /// as "nothing there" rather than "not applicable".</remarks>
    private static bool Answers(SearchQuery query, ISearchProvider provider)
        => !query.HasTags || SearchCatalog.Has(provider.Category, SearchTraits.Tagged);

    private async Task WaveAsync(
        IReadOnlyList<ISearchProvider> runnable, IReadOnlyList<SearchHit>?[] found,
        SearchQuery query, bool heavy, CancellationToken budget)
    {
        var wave = Enumerable.Range(0, runnable.Count)
            .Where(index => SearchCatalog.IsHeavy(runnable[index].Category) == heavy)
            .ToList();
        if (wave.Count == 0 || budget.IsCancellationRequested)
        {
            return;
        }
        // CancellationToken.None on the loop, the budget inside the body: an aborted loop leaves its siblings
        // unawaited, and an abandoned provider task resurfaces later as an unobserved exception with no search
        // left to attribute it to
        await Parallel.ForEachAsync(
            wave,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(_options.MaxConcurrency, 1, 16),
                CancellationToken = CancellationToken.None,
            },
            async (index, _) => found[index] = await RunAsync(runnable[index], query, budget));
    }

    /// <summary>One provider under its own ceiling. Never throws — a category that did not finish is a named gap
    /// in the result, not a failed search.</summary>
    private async Task<IReadOnlyList<SearchHit>?> RunAsync(ISearchProvider provider, SearchQuery query, CancellationToken budget)
    {
        using var own = CancellationTokenSource.CreateLinkedTokenSource(budget);
        own.CancelAfter(TimeSpan.FromMilliseconds(Math.Max(250, _options.ProviderBudgetMs)));
        try
        {
            return await provider.SearchAsync(query, own.Token);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Suche: Kategorie {Category} fehlgeschlagen.", provider.Category);
            return null;
        }
    }
}
