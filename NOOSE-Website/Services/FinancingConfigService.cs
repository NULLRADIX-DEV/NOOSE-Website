using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IFinancingConfigService" />
public class FinancingConfigService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : IFinancingConfigService
{
    private const string SettingKey = "FinanzierungsBudgets";
    private const string CacheKey = "finanzierung:budgets";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Audit entity type for the config row; SystemSetting is not auditable on its own.</summary>
    public const string AuditType = "FinancingBudgetConfig";

    public async Task<FinancingBudgetConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out FinancingBudgetConfig? cached) && cached is not null)
        {
            return cached;
        }
        var config = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public Task<FinancingBudgetConfig> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken); // always fresh

    private async Task<FinancingBudgetConfig> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = (await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return FinancingBudgetConfig.Default();
        }
        try
        {
            var config = JsonSerializer.Deserialize<FinancingBudgetConfig>(raw) ?? FinancingBudgetConfig.Default();
            // a stored config that violates the invariants would hand out impossible budgets → fall back
            Validate(config);
            return config;
        }
        catch (JsonException)
        {
            return FinancingBudgetConfig.Default();
        }
        catch (InvalidOperationException)
        {
            return FinancingBudgetConfig.Default();
        }
    }

    public async Task SaveAsync(FinancingBudgetConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
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
        // SystemSetting carries no per-field audit, so record the config action explicitly
        db.AuditLogs.Add(ManualAudit.Row(AuditType, "global", AuditAction.Modified, actor,
            ManualAudit.Change("Finanzierungs-Budgets", null, Summarise(config))));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Validate config invariants.</summary>
    public static void Validate(FinancingBudgetConfig config)
    {
        ValidateTable(config.Ranks, null);
        foreach (var (key, table) in config.Divisions)
        {
            var division = DivisionDisplay.All.FirstOrDefault(d => FinancingBudgetConfig.DivisionKey(d) == key);
            if (division == default)
            {
                throw new InvalidOperationException($"Unbekannte Abteilung „{key}“ in den Finanzierungs-Budgets.");
            }
            ValidateTable(table, division);
        }
    }

    private static void ValidateTable(Dictionary<string, FinancingRankBudget> table, Division? division)
    {
        foreach (var rank in RankDisplay.All)
        {
            if (!table.TryGetValue(FinancingBudgetConfig.RankKey(rank), out var budget))
            {
                continue;
            }
            var name = RankDisplay.Name(rank, division)
                + (division is { } d ? $" ({DivisionDisplay.Short(d)})" : string.Empty);
            if (budget.BaseMonthly < 0)
            {
                throw new InvalidOperationException($"Das Monatsbudget für {name} darf nicht negativ sein.");
            }
            if (budget.CarryOverPercent is < 0 or > 100)
            {
                throw new InvalidOperationException($"Der Übertrag für {name} muss zwischen 0 und 100 % liegen.");
            }
        }
    }

    private static string Summarise(FinancingBudgetConfig config)
        => string.Join(" | ", DivisionDisplay.All.Select(d => DivisionDisplay.Short(d) + " " + string.Join(" · ",
            RankDisplay.All.Where(r => DivisionRules.MayHold(d, r)).Select(r =>
            {
                var budget = config.For(r, d);
                return $"{RankDisplay.Name(r, d)}: {Money.Format(budget.BaseMonthly)} / {budget.CarryOverPercent} %";
            }))));
}
