using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Decorations;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Decorations;

namespace NOOSE_Website.Services.Public;

/// <inheritdoc />
public class PublicLeadershipService(
    IDbContextFactory<AppDbContext> dbFactory,
    IPublicModuleService modules,
    IPublicLeadershipPhotoStorageService storage,
    IMemoryCache cache) : IPublicLeadershipService
{
    private const string CacheKey = "public:leadership";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);

    private const int MaxNameLength = 128;
    private const int MaxRoleLength = 160;

    // ---- outward ----

    public async Task<IReadOnlyList<PublicLeadershipCard>> GetPublicAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsLiveAsync(cancellationToken))
        {
            return [];
        }
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<PublicLeadershipCard>? cached) && cached is not null)
        {
            return cached;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.OeffentlicheFuehrungsprofile.AsNoTracking()
            .Where(p => p.PublishedAt != null)
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName)
            .Select(p => new { p.PublicKey, p.DisplayName, p.Title, p.RoleText, HasPhoto = p.PhotoFileName != null,
                p.DecorationsJson })
            .ToListAsync(cancellationToken);
        // a revocation that raced the editor or failed its cleanup must still never show outside
        var copied = rows.SelectMany(p => DecorationRules.ParseCopies(p.DecorationsJson)).Select(c => c.AwardId)
            .Distinct().ToList();
        var standing = copied.Count == 0
            ? new HashSet<string>()
            : (await db.DecorationAwards.AsNoTracking()
                .Where(a => copied.Contains(a.Id) && a.RevokedAt == null)
                .Select(a => a.Id)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        // the copy carries award ids for the editor; outwards only name, symbol and grade leave
        var cards = rows
            .Select(p => new PublicLeadershipCard(p.PublicKey, p.DisplayName, p.Title, p.RoleText, p.HasPhoto,
                DecorationRules.ParseCopies(p.DecorationsJson)
                    .Where(c => standing.Contains(c.AwardId))
                    .Select(c => new PublicLeadershipDecoration(c.Name, c.Symbol, c.Tier))
                    .ToList()))
            .ToList();

        cache.Set(CacheKey, (IReadOnlyList<PublicLeadershipCard>)cards, CacheFor);
        return cards;
    }

    /// <inheritdoc />
    /// <remarks>
    /// One answer for every miss — unknown id, unreleased entry, module off, kill switch, missing file. Anything
    /// that tells them apart turns the endpoint into an existence oracle, exactly as on the wanted photo.
    /// </remarks>
    public async Task<PublicLeadershipPhoto?> GetPublishedPhotoAsync(string key,
        CancellationToken cancellationToken = default)
    {
        if (!await IsLiveAsync(cancellationToken))
        {
            return null;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFuehrungsprofile.AsNoTracking()
            .Where(p => p.PublicKey == key && p.PublishedAt != null && p.PhotoFileName != null)
            .Select(p => new { p.PhotoFileName, p.PhotoContentType })
            .FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? null
            : new PublicLeadershipPhoto(row.PhotoFileName!, row.PhotoContentType ?? "application/octet-stream");
    }

    // ---- editorial ----

    public async Task<IReadOnlyList<PublicLeadershipEdit>> GetAllAsync(ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.OeffentlicheFuehrungsprofile.AsNoTracking()
            .OrderBy(p => p.SortOrder)
            .ThenBy(p => p.DisplayName)
            .Select(p => new
            {
                p.Id, p.PublicKey, p.AgentId, Codename = p.Agent!.Codename, p.DisplayName, p.Title, p.RoleText,
                p.SortOrder, HasPhoto = p.PhotoFileName != null, p.PublishedAt,
                PublishedBy = db.Users.Where(u => u.Id == p.PublishedById).Select(u => u.Codename).FirstOrDefault(),
                p.DecorationsJson,
            })
            .ToListAsync(cancellationToken);
        return rows
            .Select(p => new PublicLeadershipEdit(p.Id, p.PublicKey, p.AgentId, p.Codename, p.DisplayName, p.Title,
                p.RoleText, p.SortOrder, p.HasPhoto, p.PublishedAt, p.PublishedBy,
                DecorationRules.ParseCopies(p.DecorationsJson).Select(c => c.AwardId).ToList()))
            .ToList();
    }

    public async Task<string> SaveAsync(PublicLeadershipInput input, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // write guard first: otherwise the read-only supervision gets as far as the roster check
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        var name = Clean(input.DisplayName, MaxNameLength);
        if (name.Length == 0)
        {
            throw new InvalidOperationException("Bitte einen Namen angeben.");
        }
        var title = Clean(input.Title, MaxNameLength);
        if (title.Length == 0)
        {
            throw new InvalidOperationException("Bitte eine Dienstgradbezeichnung angeben.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = string.IsNullOrWhiteSpace(input.Id)
            ? null
            : await db.OeffentlicheFuehrungsprofile.FirstOrDefaultAsync(p => p.Id == input.Id, cancellationToken);

        // only when the pointer actually changes. The entry is a snapshot that outlives the account it was made
        // from, so re-validating an unchanged AgentId would lock the editor out of a published entry the moment
        // its agent is terminated or flagged IsTeamLead — including out of fixing a typo in the released name.
        if (row is null || !string.Equals(row.AgentId, input.AgentId, StringComparison.Ordinal))
        {
            // AgentSelection decides who may appear at all; a raw id off the wire must not get past it, and the
            // rank floor sits on top of it rather than inside it
            var eligible = await db.Users.OnlySelectable()
                .AnyAsync(u => u.Id == input.AgentId && u.Rank != null
                    && u.Rank >= LeadershipChart.RankFloor, cancellationToken);
            if (!eligible)
            {
                throw new InvalidOperationException(
                    "Nur aktive Agenten ab Supervisory Special Agent können in das öffentliche Organigramm.");
            }
        }

        var agentChanged = row is not null && !string.Equals(row.AgentId, input.AgentId, StringComparison.Ordinal);
        if (row is null)
        {
            row = new OeffentlichesFuehrungsprofil();
            db.OeffentlicheFuehrungsprofile.Add(row);
        }
        if (input.DecorationAwardIds is not null)
        {
            row.DecorationsJson = await CopyDecorationsAsync(db, input.AgentId, input.DecorationAwardIds, cancellationToken);
        }
        else if (agentChanged)
        {
            // copies of one agent's awards never travel to someone else
            row.DecorationsJson = null;
        }
        row.AgentId = input.AgentId;
        row.DisplayName = name;
        row.Title = title;
        row.RoleText = Clean(input.RoleText, MaxRoleLength) is { Length: > 0 } role ? role : null;
        row.SortOrder = Math.Clamp(input.SortOrder, 0, 9999);
        await db.SaveChangesAsync(cancellationToken);

        Invalidate();
        return row.Id;
    }

    public async Task SetPhotoAsync(string id, Stream content, string contentType, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        if (!storage.IsAllowedType(contentType))
        {
            throw new InvalidOperationException("Dieses Bildformat wird nicht unterstützt.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFuehrungsprofile.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Eintrag nicht gefunden.");

        var previous = row.PhotoFileName;
        // a COPY under the public path; the agent's own avatar stays behind Policies.ActiveAgent
        row.PhotoFileName = await storage.SaveAsync(content, contentType, cancellationToken);
        row.PhotoContentType = contentType;
        await db.SaveChangesAsync(cancellationToken);

        if (previous is not null)
        {
            try { storage.Delete(previous); }
            catch { /* best effort */ }
        }
        Invalidate();
    }

    /// <inheritdoc />
    /// <remarks>Releasing needs a living module; withdrawing must work even with everything switched off.</remarks>
    public async Task PublishAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);
        await modules.RequireEnabledAsync(PublicModules.Leadership, cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFuehrungsprofile.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Eintrag nicht gefunden.");
        row.PublishedAt = DateTime.UtcNow;
        row.PublishedById = actor.GetAgentId();
        await db.SaveChangesAsync(cancellationToken);
        Invalidate();
    }

    public async Task RetractAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFuehrungsprofile.FirstOrDefaultAsync(p => p.Id == id, cancellationToken)
            ?? throw new InvalidOperationException("Eintrag nicht gefunden.");
        row.PublishedAt = null;
        row.PublishedById = null;
        await db.SaveChangesAsync(cancellationToken);
        Invalidate();
    }

    /// <inheritdoc />
    /// <remarks>A hard delete: the entry is a publication, not a record, and its photo goes with it.</remarks>
    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireWriteAccess(actor);
        Permission.RequireLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.OeffentlicheFuehrungsprofile.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (row is null)
        {
            return;
        }
        var photo = row.PhotoFileName;
        db.OeffentlicheFuehrungsprofile.Remove(row);
        await db.SaveChangesAsync(cancellationToken);

        if (photo is not null)
        {
            try { storage.Delete(photo); }
            catch { /* best effort */ }
        }
        Invalidate();
    }

    /// <inheritdoc />
    /// <remarks>Callers gate: the decoration service runs it after its own write guard.</remarks>
    public async Task SyncDecorationAsync(string awardId, PublicDecorationCopy? copy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.OeffentlicheFuehrungsprofile
            .Where(p => p.DecorationsJson != null && p.DecorationsJson.Contains(awardId))
            .ToListAsync(cancellationToken);
        var touched = false;
        foreach (var row in rows)
        {
            var json = DecorationRules.ReplaceCopy(row.DecorationsJson, awardId, copy, out var changed);
            if (changed)
            {
                row.DecorationsJson = json;
                touched = true;
            }
        }
        if (touched)
        {
            await db.SaveChangesAsync(cancellationToken);
            Invalidate();
        }
    }

    /// <summary>Copies the chosen awards that still stand and belong to this agent.</summary>
    private static async Task<string?> CopyDecorationsAsync(AppDbContext db, string agentId,
        IReadOnlyCollection<string> awardIds, CancellationToken cancellationToken)
    {
        if (awardIds.Count == 0)
        {
            return null;
        }
        var ids = awardIds.Distinct(StringComparer.Ordinal).ToList();
        var rows = await db.DecorationAwards.AsNoTracking()
            .Where(a => ids.Contains(a.Id) && a.AgentId == agentId && a.RevokedAt == null)
            .OrderBy(a => a.AwardedOn)
            .Select(a => new { a.Id, a.Decoration!.Name, a.Decoration.Symbol, a.Decoration.Tier, a.AwardedOn })
            .ToListAsync(cancellationToken);
        return DecorationRules.CopiesJson(rows
            .Select(a => new PublicDecorationCopy(a.Id, a.Name, a.Symbol, a.Tier, a.AwardedOn.Year))
            .ToList());
    }

    /// <summary>Module on and kill switch off; the same pair every outward read asks.</summary>
    private async Task<bool> IsLiveAsync(CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await modules.GetAsync(cancellationToken);
            return snapshot.IsEnabled(PublicModules.Leadership);
        }
        catch
        {
            /* best effort: an unreachable snapshot reads as off */
            return false;
        }
    }

    private void Invalidate() => cache.Remove(CacheKey);

    private static string Clean(string? value, int max)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length <= max ? text : text[..max];
    }
}
