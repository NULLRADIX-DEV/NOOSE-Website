using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPartnerVisibilityPolicyService" />
public class PartnerVisibilityPolicyService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : IPartnerVisibilityPolicyService
{
    private const string SettingKey = "PartnerRangSichtbarkeit";
    private const string CacheKey = "PartnerRangSichtbarkeitConfig";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<PartnerVisibilityConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out PartnerVisibilityConfig? cfg) && cfg is not null)
        {
            return cfg;
        }
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var raw = (await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
            cfg = Parse(raw);
        }
        catch (Exception)
        {
            /* best effort */
            return new PartnerVisibilityConfig();
        }
        cache.Set(CacheKey, cfg, CacheDuration);
        return cfg;
    }

    public async Task<PartnerRankVisibility?> GetRankAsync(PartnerAgency agency, PartnerRank rank, CancellationToken cancellationToken = default)
    {
        var cfg = await GetAsync(cancellationToken);
        return cfg.Ranks.GetValueOrDefault(PartnerVisibilityConfig.RankKey(agency, rank));
    }

    public async Task SaveRankAsync(PartnerAgency agency, PartnerRank rank, PartnerRankVisibility? visibility, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken);
        var cfg = Parse(row?.Value);

        var key = PartnerVisibilityConfig.RankKey(agency, rank);
        if (visibility is null)
        {
            cfg.Ranks.Remove(key);
        }
        else
        {
            cfg.Ranks[key] = visibility;
        }

        var json = JsonSerializer.Serialize(cfg);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = json });
        }
        else
        {
            row.Value = json;
        }
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task<IReadOnlySet<string>?> GetAllowedTypesAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (user.GetPartnerAgency() is not { } agency)
        {
            return null;
        }
        var meId = user.GetAgentId();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // released types
        var shared = await db.PartnerShares
            .Where(s => s.Agency == agency && (s.PartnerAgentId == null || s.PartnerAgentId == meId))
            .Select(s => new { s.EntityType, Individual = s.PartnerAgentId != null })
            .Distinct()
            .ToListAsync(cancellationToken);
        var ruled = await db.PartnerReleaseRules
            .Where(r => r.Agency == agency)
            .Select(r => r.EntityType)
            .ToListAsync(cancellationToken);
        var released = new HashSet<string>(shared.Select(s => s.EntityType).Concat(ruled)) { nameof(Document) };

        var cfg = await GetAsync(cancellationToken);
        var entry = user.GetPartnerRank() is { } rank
            ? cfg.Ranks.GetValueOrDefault(PartnerVisibilityConfig.RankKey(agency, rank))
            : null;
        // unconfigured: all released
        if (entry is null)
        {
            return PartnerTabCatalog.All.Select(t => t.TypeKey).Where(released.Contains).ToHashSet();
        }

        var allowed = new HashSet<string>(entry.Types);
        // individual account releases widen beyond the rank default
        foreach (var type in shared.Where(s => s.Individual).Select(s => s.EntityType))
        {
            allowed.Add(type);
        }
        allowed.IntersectWith(released);
        return allowed;
    }

    public async Task<IReadOnlySet<string>?> GetVisibleTabsAsync(ClaimsPrincipal user, string typeKey, string recordId, CancellationToken cancellationToken = default)
    {
        if (user.GetPartnerAgency() is not { } agency)
        {
            return null;
        }
        var tabs = await RankTabsAsync(user, agency, typeKey, recordId, cancellationToken);

        // blocks beat releases
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var blocked = await PartnerVisibility.BlockedContentAsync(db, agency, cancellationToken);
        if (blocked == PartnerContent.None)
        {
            return tabs;
        }
        var visible = new HashSet<string>(tabs ?? PartnerTabCatalog.TabSlugs(typeKey));
        visible.ExceptWith(PartnerContentCatalog.BlockedTabs(blocked));
        return visible;
    }

    /// <summary>Rank allowlist for a record; null = all tabs (unconfigured rank or individually released).</summary>
    private async Task<IReadOnlySet<string>?> RankTabsAsync(ClaimsPrincipal user, PartnerAgency agency, string typeKey, string recordId, CancellationToken cancellationToken)
    {
        if (user.GetPartnerRank() is not { } rank)
        {
            return null;
        }
        var cfg = await GetAsync(cancellationToken);
        if (cfg.Ranks.GetValueOrDefault(PartnerVisibilityConfig.RankKey(agency, rank)) is not { } entry)
        {
            return null;
        }

        // individually released record shows in full, regardless of the rank default
        var meId = user.GetAgentId();
        if (meId is not null)
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var individual = await db.PartnerShares.AnyAsync(
                s => s.EntityType == typeKey && s.EntityId == recordId && s.PartnerAgentId == meId && s.Agency == agency,
                cancellationToken);
            if (individual)
            {
                return null;
            }
        }

        if (entry.Tabs.TryGetValue(typeKey, out var slugs))
        {
            return new HashSet<string>(slugs);
        }
        // listed type without a tab restriction = all tabs; unlisted type = no tabs
        return entry.Types.Contains(typeKey) ? null : new HashSet<string>();
    }

    private static PartnerVisibilityConfig Parse(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new PartnerVisibilityConfig()
            : JsonSerializer.Deserialize<PartnerVisibilityConfig>(raw) ?? new PartnerVisibilityConfig();
}
