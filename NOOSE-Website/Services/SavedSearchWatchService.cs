using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ISavedSearchWatchService" />
/// <remarks>
/// The owner's principal is rebuilt from the account on every run, so a demotion, a lost flag or a termination
/// takes effect on the next run without anyone touching the watch. Only the stored hit keys outlive a rights
/// change, and a changed <see cref="SavedSearchRules.ScopeStamp"/> turns them into a silent baseline.
/// </remarks>
public class SavedSearchWatchService(
    IDbContextFactory<AppDbContext> dbFactory,
    ISearchService search,
    IAgentPrincipalService principals,
    INotificationService notifications,
    ILogger<SavedSearchWatchService> logger) : ISavedSearchWatchService
{
    public async Task<int> RunDueAsync(DateTime nowUtc, CancellationToken cancellationToken = default)
    {
        // the claim stamp is compared for equality later; whole seconds survive any column precision
        nowUtc = new DateTime(nowUtc.Ticks - nowUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var dueBefore = nowUtc - SavedSearchRules.WatchInterval;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var due = await db.SavedSearch.AsNoTracking()
            .Where(g => g.IsWatched && (g.LastCheckedAt == null || g.LastCheckedAt <= dueBefore))
            .OrderBy(g => g.LastCheckedAt)
            .Take(SavedSearchRules.WatchBatch)
            .Select(g => new { g.Id, g.LastCheckedAt })
            .ToListAsync(cancellationToken);

        var reported = 0;
        foreach (var row in due)
        {
            // a stopping host leaves the rest unclaimed for the next run
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            try
            {
                // claim first, so a second instance or an overlapping tick never runs it twice
                var claimed = await db.SavedSearch
                    .Where(g => g.Id == row.Id && g.IsWatched && g.LastCheckedAt == row.LastCheckedAt)
                    .ExecuteUpdateAsync(s => s.SetProperty(g => g.LastCheckedAt, nowUtc), CancellationToken.None);
                if (claimed > 0 && await RunAsync(db, row.Id, nowUtc, cancellationToken))
                {
                    reported++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                /* the claim is spent; the next due run compares against the same keys and loses nothing */
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Watched search {Id} failed.", row.Id);
            }
        }
        return reported;
    }

    /// <summary>One claimed run; true when it reported new hits.</summary>
    /// <remarks>Every write is conditioned on the claim stamp: switching the watch off or on again resets it, and a
    /// run that started before must then neither write its keys into the fresh baseline nor report.</remarks>
    private async Task<bool> RunAsync(AppDbContext db, string id, DateTime claimedAt, CancellationToken cancellationToken)
    {
        var entry = await db.SavedSearch.AsNoTracking()
            .Where(g => g.Id == id)
            .Select(g => new { g.AgentId, g.Name, g.SearchParameterJson, g.KnownHitsJson, g.ScopeStamp })
            .FirstOrDefaultAsync(CancellationToken.None);
        if (entry is null || entry.AgentId == DemoIdentity.AgentId)
        {
            return false;
        }

        // today's rights, not the ones from when the watch was switched on
        var owner = await principals.BuildAsync(entry.AgentId, CancellationToken.None);
        if (owner is null || !SavedSearchRules.MayWatch(owner)
            || SavedSearchRules.Parse(entry.SearchParameterJson) is not { } criteria
            || !SavedSearchRules.IsWatchable(criteria))
        {
            return false;
        }

        var results = await search.SearchAsync(SavedSearchRules.ForWatch(criteria), owner, cancellationToken);
        var stamp = SavedSearchRules.ScopeStamp(ViewerScope.From(owner));
        var known = entry.KnownHitsJson is null ? null : SavedSearchRules.ReadKeys(entry.KnownHitsJson);
        var baseline = known is null || entry.ScopeStamp != stamp;
        var outcome = SavedSearchRules.Compare(known, baseline, results);
        // a cut-short baseline missed hits a later full run would report as new: the next run baselines once more.
        // Only once, or a search that always runs out of time would never report at all
        var degraded = results.Incomplete.Count > 0 || results.SideIndexSkipped;
        var retried = entry.ScopeStamp == SavedSearchRules.RetryStamp(stamp);
        var keepStamp = baseline && degraded && !retried ? SavedSearchRules.RetryStamp(stamp) : stamp;

        // derived state of a run, like the reminder stamps: no audit row, no modified stamp
        var written = await db.SavedSearch
            .Where(g => g.Id == id && g.IsWatched && g.LastCheckedAt == claimedAt)
            .ExecuteUpdateAsync(s => s
                .SetProperty(g => g.KnownHitsJson, SavedSearchRules.WriteKeys(outcome.Known))
                .SetProperty(g => g.ScopeStamp, keepStamp)
                .SetProperty(g => g.LastRunIncomplete, degraded), CancellationToken.None);
        if (written == 0 || outcome.Fresh.Count == 0)
        {
            return false;
        }

        var unseen = await AddUnseenAsync(db, id, claimedAt, outcome.Fresh.Select(SavedSearchRules.Key));
        if (unseen is null)
        {
            return false;
        }

        // folds onto the still-unread report, so the bell shows one line per search; only a fresh line pings
        await notifications.NotifyManyOnceAsync([entry.AgentId], NotificationType.SavedSearchHit,
            SavedSearchRules.Title(entry.Name, unseen.Value, outcome.Fresh), SavedSearchRules.Href(id),
            triggerId: null, CancellationToken.None);
        return true;
    }

    /// <summary>Adds the fresh keys to the marks; the new mark count, or null when the claim was lost.</summary>
    /// <remarks>Compare-and-set against the marks as read: the owner may clear them on the page meanwhile, and a blind
    /// write would bring back what they already looked at.</remarks>
    private static async Task<int?> AddUnseenAsync(AppDbContext db, string id, DateTime claimedAt, IEnumerable<string> fresh)
    {
        var keys = fresh.ToList();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var current = await db.SavedSearch.AsNoTracking()
                .Where(g => g.Id == id)
                .Select(g => g.UnseenHitsJson)
                .FirstOrDefaultAsync(CancellationToken.None);
            var merged = SavedSearchRules.AddUnseen(SavedSearchRules.ReadKeys(current), keys);
            var json = SavedSearchRules.WriteKeys(merged);
            var written = await db.SavedSearch
                .Where(g => g.Id == id && g.IsWatched && g.LastCheckedAt == claimedAt && g.UnseenHitsJson == current)
                .ExecuteUpdateAsync(s => s.SetProperty(g => g.UnseenHitsJson, json), CancellationToken.None);
            if (written > 0)
            {
                return merged.Count;
            }
        }
        return null;
    }
}
