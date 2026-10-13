using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ISavedSearchService" />
public class SavedSearchService(IDbContextFactory<AppDbContext> dbFactory) : ISavedSearchService
{
    public async Task<List<SavedSearch>> GetForAgentAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // without the watch's hit keys: up to thousands per search, and only the worker reads them
        return await db.SavedSearch
            .Where(g => g.AgentId == agentId)
            .OrderBy(g => g.Name)
            .Select(g => new SavedSearch
            {
                Id = g.Id,
                AgentId = g.AgentId,
                Name = g.Name,
                SearchParameterJson = g.SearchParameterJson,
                IsWatched = g.IsWatched,
                LastCheckedAt = g.LastCheckedAt,
                UnseenHitsJson = g.UnseenHitsJson,
                LastRunIncomplete = g.LastRunIncomplete,
                CreatedAt = g.CreatedAt,
                CreatedById = g.CreatedById,
                ModifiedAt = g.ModifiedAt,
                ModifiedById = g.ModifiedById,
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<SavedSearch> SaveAsync(string agentId, string name, SearchCriteria criteria, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        name = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Bitte einen Namen für die Suche angeben.");
        }
        if (string.IsNullOrWhiteSpace(agentId))
        {
            throw new InvalidOperationException("Kein angemeldeter Agent.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = new SavedSearch
        {
            AgentId = agentId,
            Name = name,
            SearchParameterJson = JsonSerializer.Serialize(criteria),
        };
        db.SavedSearch.Add(entry);
        await db.SaveChangesAsync(cancellationToken);
        return entry;
    }

    public async Task DeleteAsync(string id, string agentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // only own searches deletable
        var entry = await db.SavedSearch.FirstOrDefaultAsync(g => g.Id == id && g.AgentId == agentId, cancellationToken);
        if (entry is null)
        {
            return;
        }
        db.SavedSearch.Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SetWatchedAsync(string id, bool watched, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        if (!SavedSearchRules.MayWatch(actor))
        {
            throw new UnauthorizedAccessException("Gespeicherte Suchen überwachen können nur interne Agenten.");
        }
        var me = actor.GetAgentId();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entry = await db.SavedSearch.FirstOrDefaultAsync(g => g.Id == id && g.AgentId == me, cancellationToken)
            ?? throw new InvalidOperationException("Diese gespeicherte Suche gibt es nicht mehr.");
        if (entry.IsWatched == watched)
        {
            return;
        }

        if (watched)
        {
            if (SavedSearchRules.Parse(entry.SearchParameterJson) is not { } criteria || !SavedSearchRules.IsWatchable(criteria))
            {
                throw new InvalidOperationException(
                    "Überwachen lässt sich nur eine Suche mit Suchbegriff (mindestens zwei Zeichen) oder Stichwort, "
                    + "die nicht auf eigene Einträge oder Protokolle eingegrenzt ist.");
            }
            var watching = await db.SavedSearch.CountAsync(g => g.AgentId == me && g.IsWatched, cancellationToken);
            if (watching >= SavedSearchRules.MaxWatchedPerAgent)
            {
                throw new InvalidOperationException(
                    $"Du überwachst schon {SavedSearchRules.MaxWatchedPerAgent} Suchen. Schalte erst eine davon ab.");
            }
        }

        entry.IsWatched = watched;
        // either way the next watch starts over: a baseline, no marks, no stale stamp
        entry.LastCheckedAt = null;
        entry.KnownHitsJson = null;
        entry.UnseenHitsJson = null;
        entry.ScopeStamp = null;
        entry.LastRunIncomplete = false;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkSeenAsync(string id, string? shownUnseenJson, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        if (!actor.MayWrite() || string.IsNullOrEmpty(shownUnseenJson) || actor.GetAgentId() is not { } me)
        {
            return;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // a read marker: no audit row, no modified stamp; a run that added marks meanwhile wins
        await db.SavedSearch
            .Where(g => g.Id == id && g.AgentId == me && g.UnseenHitsJson == shownUnseenJson)
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.UnseenHitsJson, (string?)null), cancellationToken);
    }
}
