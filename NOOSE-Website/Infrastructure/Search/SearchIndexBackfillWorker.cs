using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Search;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;

namespace NOOSE_Website.Infrastructure.Search;

/// <summary>One-shot startup backfill: builds the phonetic/stem side-index for all existing records when the index
/// is still empty (fresh deploy). Ongoing changes are handled by <see cref="SearchIndexInterceptor"/>.</summary>
public sealed class SearchIndexBackfillWorker(IServiceScopeFactory scopeFactory, ILogger<SearchIndexBackfillWorker> logger)
    : BackgroundService
{
    /// <summary>Bump whenever <see cref="SearchIndexProjection"/> gains or changes a type. A stored version below this
    /// re-runs the whole pass; the pass wipes both tables first, so re-running is idempotent. Without this an existing
    /// installation would index zero rows of a newly added type — phonetic recall would just look flaky for months.</summary>
    public const int Version = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
            await using var db = await factory.CreateDbContextAsync(stoppingToken);

            // guard on a completion marker, not table emptiness: a mid-backfill crash or a concurrent live edit in the
            // startup window would otherwise leave a non-empty table that permanently skips the rest of the corpus
            var marker = await db.SystemSettings
                .FirstOrDefaultAsync(s => s.Key == SystemSettingKeys.SearchIndexBackfillDone, stoppingToken);
            // legacy rows store "true", which never parses -> they re-run once against the current version
            if (marker is not null && int.TryParse(marker.Value, out var stored) && stored >= Version)
            {
                return; // this version already completed — never re-scan on later starts
            }

            // wipe any partial rows (from a prior aborted run or interceptor writes during the delay) and rebuild
            // from scratch so the pass is idempotent; the records themselves are re-indexed below
            await db.SearchPhoneticKeys.ExecuteDeleteAsync(stoppingToken);
            await db.SearchStemTokens.ExecuteDeleteAsync(stoppingToken);

            var total = 0;
            total += await IndexAllAsync(db, db.People, stoppingToken);
            total += await IndexAllAsync(db, db.PersonAliases, stoppingToken);
            total += await IndexAllAsync(db, db.Factions, stoppingToken);
            total += await IndexAllAsync(db, db.PersonGroups, stoppingToken);
            total += await IndexAllAsync(db, db.Parties, stoppingToken);
            total += await IndexAllAsync(db, db.Operations, stoppingToken);
            total += await IndexAllAsync(db, db.Taskforces, stoppingToken);
            total += await IndexAllAsync(db, db.Cases, stoppingToken);
            total += await IndexAllAsync(db, db.Jobs, stoppingToken);
            total += await IndexAllAsync(db, db.Users, stoppingToken);
            total += await IndexAllAsync(db, db.Laws, stoppingToken);
            total += await IndexAllAsync(db, db.EvidenceItems, stoppingToken);
            total += await IndexAllAsync(db, db.HandbuchArtikel, stoppingToken);
            total += await IndexAllAsync(db, db.HandbuchBegriffe, stoppingToken);

            // mark done only after every type is indexed, so an abort before here re-runs the whole pass next start
            if (marker is null)
            {
                db.SystemSettings.Add(new SystemSetting
                {
                    Key = SystemSettingKeys.SearchIndexBackfillDone, Value = Version.ToString(),
                });
            }
            else
            {
                // update, never Add: a re-run reaches this line with the row already present
                marker.Value = Version.ToString();
            }
            await db.SaveChangesAsync(stoppingToken);

            if (total > 0)
            {
                logger.LogInformation("Search index backfill: {Count} records indexed.", total);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            /* shutting down */
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search index backfill failed.");
        }
    }

    // index one entity set; the interceptor ignores the index rows we add (not an indexed type), so this is a plain insert
    private static async Task<int> IndexAllAsync<T>(AppDbContext db, IQueryable<T> set, CancellationToken ct) where T : class
    {
        var items = await set.AsNoTracking().ToListAsync(ct);
        foreach (var item in items)
        {
            if (SearchIndexProjection.For(item) is not { } row)
            {
                continue;
            }
            foreach (var key in row.PhoneticKeys)
            {
                db.Add(new SearchPhoneticKey { EntityType = row.EntityType, EntityId = row.EntityId, SourceId = row.SourceId, Key = key });
            }
            foreach (var stem in row.Stems)
            {
                db.Add(new SearchStemToken { EntityType = row.EntityType, EntityId = row.EntityId, SourceId = row.SourceId, Stem = stem });
            }
        }
        if (items.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
        return items.Count;
    }
}
