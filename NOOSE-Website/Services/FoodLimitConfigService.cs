using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Food;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFoodLimitConfigService" />
public class FoodLimitConfigService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : IFoodLimitConfigService
{
    private const string SettingKey = "VerpflegungsLimits";
    private const string CacheKey = "verpflegung:limits";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Audit entity type for the config row; SystemSetting is not auditable on its own.</summary>
    public const string AuditType = "FoodLimitConfig";

    public async Task<FoodLimitConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out FoodLimitConfig? cached) && cached is not null)
        {
            return cached;
        }
        var config = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public Task<FoodLimitConfig> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken);

    private async Task<FoodLimitConfig> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = (await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FoodLimitConfig.Default();
        }
        try
        {
            var config = JsonSerializer.Deserialize<FoodLimitConfig>(raw) ?? FoodLimitConfig.Default();
            // invalid stored rules would hand out impossible limits
            Validate(config);
            return config;
        }
        catch (JsonException)
        {
            return FoodLimitConfig.Default();
        }
        catch (InvalidOperationException)
        {
            return FoodLimitConfig.Default();
        }
    }

    public async Task SaveAsync(FoodLimitConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);
        Validate(config);

        var json = JsonSerializer.Serialize(config);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = json });
        }
        else
        {
            row.Value = json;
        }
        // SystemSetting carries no per-field audit
        db.AuditLogs.Add(ManualAudit.Row(AuditType, "global", AuditAction.Modified, actor,
            ManualAudit.Change("Verpflegungs-Limits", null, Summarise(config))));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Validate config invariants.</summary>
    public static void Validate(FoodLimitConfig config)
    {
        ValidateTable(config.Ranks, null);
        foreach (var (key, table) in config.Divisions)
        {
            var division = DivisionDisplay.All.FirstOrDefault(d => FoodLimitConfig.DivisionKey(d) == key);
            if (division == default)
            {
                throw new InvalidOperationException($"Unbekannte Abteilung „{key}“ in den Verpflegungs-Limits.");
            }
            ValidateTable(table, division);
        }
    }

    private static void ValidateTable(Dictionary<string, FoodRankLimit> table, Division? division)
    {
        foreach (var rank in RankDisplay.All)
        {
            if (!table.TryGetValue(FoodLimitConfig.RankKey(rank), out var limit))
            {
                continue;
            }
            var name = RankDisplay.Name(rank, division)
                + (division is { } d ? $" ({DivisionDisplay.Short(d)})" : string.Empty);
            if (limit.FoodPerWeek < 0 || limit.DrinkPerWeek < 0)
            {
                throw new InvalidOperationException($"Die Wochenlimits für {name} dürfen nicht negativ sein.");
            }
        }
    }

    private static string Summarise(FoodLimitConfig config)
        => string.Join(" | ", DivisionDisplay.All.Select(d => DivisionDisplay.Short(d) + " " + string.Join(" · ",
            RankDisplay.All.Where(r => DivisionRules.MayHold(d, r)).Select(r =>
            {
                var limit = config.For(r, d);
                return $"{RankDisplay.Name(r, d)}: {limit.FoodPerWeek} Essen / {limit.DrinkPerWeek} Trinken";
            }))));
}
