using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IPartnerUnitService" />
public class PartnerUnitService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : IPartnerUnitService
{
    private const string CacheKey = "PartnerEinheiten";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<IReadOnlyList<PartnerUnitView>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyList<PartnerUnitView>? cached) && cached is not null)
        {
            return cached;
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var units = await LoadAsync(db, cancellationToken);
        cache.Set(CacheKey, units, CacheDuration);
        return units;
    }

    public async Task<PartnerUnitView?> GetAsync(string? id, CancellationToken cancellationToken = default)
        => id is null ? null : (await GetAllAsync(cancellationToken)).FirstOrDefault(u => u.Id == id);

    public async Task<IReadOnlyList<PartnerUnitView>> ForAgencyAsync(PartnerAgency agency, CancellationToken cancellationToken = default)
        => (await GetAllAsync(cancellationToken)).Where(u => u.Agency == agency).ToList();

    public async Task<string> SaveAsync(PartnerUnitInput input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);
        var name = (input.Name ?? string.Empty).Trim();
        if (name.Length is 0 or > 100)
        {
            throw new InvalidOperationException("Der Name einer Einheit braucht 1 bis 100 Zeichen.");
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (await db.PartnerUnits.AnyAsync(u => u.Agency == input.Agency && u.Name == name && u.Id != input.Id, cancellationToken))
        {
            throw new InvalidOperationException("Diese Behörde hat schon eine Einheit mit dem Namen.");
        }
        PartnerUnit unit;
        if (input.Id is null)
        {
            unit = new PartnerUnit { Agency = input.Agency };
            db.PartnerUnits.Add(unit);
        }
        else
        {
            unit = await db.PartnerUnits.FirstOrDefaultAsync(u => u.Id == input.Id, cancellationToken)
                ?? throw new KeyNotFoundException("Einheit nicht gefunden.");
            if (unit.Agency != input.Agency)
            {
                throw new InvalidOperationException("Eine Einheit wechselt nicht die Behörde.");
            }
        }
        unit.Name = name;
        unit.SortOrder = input.SortOrder;
        unit.IsProsecution = input.IsProsecution;
        unit.MayCreateInvestigations = input.MayCreateInvestigations;
        unit.LeadSeesAllInvestigations = input.LeadSeesAllInvestigations;
        unit.InvestigationDefaultsJson = InvestigationTabAccess.Serialize(input.InvestigationDefaults);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
        return unit.Id;
    }

    public async Task DeleteAsync(string id, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var unit = await db.PartnerUnits.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Einheit nicht gefunden.");
        if (await db.Users.AnyAsync(a => a.PartnerUnitId == id && a.PartnerAgency != null, cancellationToken))
        {
            throw new InvalidOperationException("Die Einheit hat noch Konten. Erst die Konten einer anderen Einheit zuordnen.");
        }
        if (await db.PartnerUnitShares.AnyAsync(s => s.PartnerUnitId == id, cancellationToken)
            || await db.PartnerUnitReleaseRules.AnyAsync(r => r.PartnerUnitId == id, cancellationToken))
        {
            throw new InvalidOperationException("Die Einheit hat noch Freigaben oder Regeln. Erst diese zurücknehmen.");
        }
        db.PartnerUnits.Remove(unit);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task SaveVisibilityAsync(string id, PartnerUnitVisibility? visibility, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var unit = await db.PartnerUnits.FirstOrDefaultAsync(u => u.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException("Einheit nicht gefunden.");
        unit.VisibilityJson = visibility is null ? null : JsonSerializer.Serialize(visibility);
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    public async Task<IReadOnlyList<PartnerRuleView>> GetRulesAsync(string unitId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.PartnerUnitReleaseRules.AsNoTracking()
            .Where(r => r.PartnerUnitId == unitId)
            .Select(r => new PartnerRuleView(r.EntityType, r.Scope, r.IncludesChildren))
            .ToListAsync(cancellationToken);
    }

    public async Task SaveRulesAsync(string unitId, IReadOnlyList<PartnerRuleView> rules, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAdmin(actor);
        foreach (var rule in rules)
        {
            PartnerAgencyProfileService.Validate(rule);
        }
        if (rules.Select(r => r.EntityType).Distinct().Count() != rules.Count)
        {
            throw new ArgumentException("Je Aktentyp gibt es höchstens eine Regel.", nameof(rules));
        }
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        if (!await db.PartnerUnits.AnyAsync(u => u.Id == unitId, cancellationToken))
        {
            throw new KeyNotFoundException("Einheit nicht gefunden.");
        }
        var existing = await db.PartnerUnitReleaseRules.Where(r => r.PartnerUnitId == unitId).ToListAsync(cancellationToken);
        foreach (var rule in rules)
        {
            var row = existing.FirstOrDefault(r => r.EntityType == rule.EntityType);
            if (row is null)
            {
                db.PartnerUnitReleaseRules.Add(new PartnerUnitReleaseRule
                {
                    PartnerUnitId = unitId, EntityType = rule.EntityType, Scope = rule.Scope, IncludesChildren = rule.IncludesChildren,
                });
            }
            else
            {
                row.Scope = rule.Scope;
                row.IncludesChildren = rule.IncludesChildren;
            }
        }
        // rule rows are real deletes; the unique index would not tolerate dead rows
        db.PartnerUnitReleaseRules.RemoveRange(existing.Where(r => rules.All(n => n.EntityType != r.EntityType)));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Reads every unit with its live account count.</summary>
    internal static async Task<IReadOnlyList<PartnerUnitView>> LoadAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        var rows = await db.PartnerUnits.AsNoTracking()
            .OrderBy(u => u.Agency).ThenBy(u => u.SortOrder).ThenBy(u => u.Name)
            .ToListAsync(cancellationToken);
        var counts = (await db.Users
                .Where(a => a.PartnerUnitId != null && a.PartnerAgency != null && a.Status == AgentStatus.Active)
                .Select(a => a.PartnerUnitId!)
                .ToListAsync(cancellationToken))
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
        PartnerUnitDisplay.ReplaceAll(rows.Select(u => (u.Id, u.Name)));
        return rows.Select(u => ToView(u, counts.GetValueOrDefault(u.Id))).ToList();
    }

    /// <summary>Entity to view; broken JSON reads as unconfigured.</summary>
    internal static PartnerUnitView ToView(PartnerUnit u, int accounts = 0)
        => new(u.Id, u.Agency, u.Name, u.SortOrder, u.LegacyRank, u.IsProsecution, u.MayCreateInvestigations,
            u.LeadSeesAllInvestigations, ParseVisibility(u.VisibilityJson),
            InvestigationTabAccess.Parse(u.InvestigationDefaultsJson), accounts);

    private static PartnerUnitVisibility? ParseVisibility(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        try
        {
            return JsonSerializer.Deserialize<PartnerUnitVisibility>(raw);
        }
        catch (JsonException)
        {
            /* ignore */
            return null;
        }
    }

    /// <summary>
    /// Startup alignment, idempotent: partner accounts without a unit (an older build created them) get the seeded unit
    /// of their (agency, tier); the old per-tier allowlist moves onto the seeded units once.
    /// </summary>
    public static async Task SyncAsync(AppDbContext db, CancellationToken cancellationToken = default)
    {
        var unitIds = await db.PartnerUnits.IgnoreQueryFilters().Select(u => u.Id).ToListAsync(cancellationToken);
        var partners = await db.Users.Where(a => a.PartnerAgency != null).ToListAsync(cancellationToken);
        foreach (var agent in partners.Where(a => a.PartnerUnitId is null || !unitIds.Contains(a.PartnerUnitId)))
        {
            agent.PartnerUnitId = PartnerUnits.ForLegacy(agent.PartnerAgency!.Value, agent.PartnerRank);
        }

        const string legacyKey = "PartnerRangSichtbarkeit";
        const string doneKey = "PartnerRangSichtbarkeitUebernommen";
        if (!await db.SystemSettings.AnyAsync(s => s.Key == doneKey, cancellationToken))
        {
            var raw = (await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == legacyKey, cancellationToken))?.Value;
            PartnerVisibilityConfig? legacy = null;
            try
            {
                legacy = string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<PartnerVisibilityConfig>(raw);
            }
            catch (JsonException)
            {
                /* ignore */
            }
            if (legacy is not null)
            {
                foreach (var unit in await db.PartnerUnits.Where(u => u.VisibilityJson == null).ToListAsync(cancellationToken))
                {
                    if (unit.Id == PartnerUnits.ForLegacy(unit.Agency, unit.LegacyRank)
                        && legacy.Ranks.TryGetValue(PartnerVisibilityConfig.RankKey(unit.Agency, unit.LegacyRank), out var entry))
                    {
                        // the investigations tab is new on taskforces; an explicit old tab list would hide it
                        if (entry.Tabs.TryGetValue("Taskforce", out var tabs) && !tabs.Contains("ermittlungen"))
                        {
                            tabs.Add("ermittlungen");
                        }
                        unit.VisibilityJson = JsonSerializer.Serialize(entry);
                    }
                }
            }
            db.SystemSettings.Add(new SystemSetting { Key = doneKey, Value = "1" });
        }
        await db.SaveChangesAsync(cancellationToken);
        var names = await db.PartnerUnits.Select(u => new { u.Id, u.Name }).ToListAsync(cancellationToken);
        PartnerUnitDisplay.ReplaceAll(names.Select(u => (u.Id, u.Name)));
    }
}
