using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="ICareerRequirementsService" />
public class CareerRequirementsService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : ICareerRequirementsService
{
    public const string SettingKey = "KarriereAnforderungen";
    private const string CacheKey = "karriere:anforderungen";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Audit entity type for the config row; SystemSetting is not auditable on its own.</summary>
    public const string AuditType = "CareerRequirements";

    public async Task<CareerRequirementsConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out CareerRequirementsConfig? cached) && cached is not null)
        {
            return cached;
        }
        var config = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public Task<CareerRequirementsConfig> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken); // always fresh

    private async Task<CareerRequirementsConfig> LoadAsync(CancellationToken cancellationToken)
    {
        string? raw;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            raw = (await db.SystemSettings
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
        }
        catch (Exception)
        {
            // public page must render even without a database
            return CareerRequirementsConfig.Default();
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            return CareerRequirementsConfig.Default();
        }
        try
        {
            var config = JsonSerializer.Deserialize<CareerRequirementsConfig>(raw) ?? CareerRequirementsConfig.Default();
            Normalise(config);
            // a hand-edited row that violates the bounds would break the public page → fall back
            Validate(config);
            return config;
        }
        catch (JsonException)
        {
            return CareerRequirementsConfig.Default();
        }
        catch (InvalidOperationException)
        {
            return CareerRequirementsConfig.Default();
        }
    }

    public async Task SaveAsync(CareerRequirementsConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);
        // the read-only supervision passes the guard above, so gate the write separately
        Permission.RequireWriteAccess(actor);

        Normalise(config);
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
            ManualAudit.Change("Karriere-Anforderungen", null, Summarise(config))));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Trims every text and drops blank entries; an editor row left empty is not an error.</summary>
    public static void Normalise(CareerRequirementsConfig config)
    {
        config.Items ??= new List<CareerRequirement>();
        foreach (var item in config.Items)
        {
            item.Text = item.Text?.Trim() ?? string.Empty;
            item.Alternatives = (item.Alternatives ?? new List<string>())
                .Select(a => a?.Trim() ?? string.Empty)
                .Where(a => a.Length > 0)
                .ToList();
        }
        config.Items = config.Items.Where(i => i.Text.Length > 0).ToList();
    }

    /// <summary>Validate config invariants.</summary>
    public static void Validate(CareerRequirementsConfig config)
    {
        var items = config.Items ?? new List<CareerRequirement>();
        if (items.Count > CareerRequirementsConfig.MaxItems)
        {
            throw new InvalidOperationException(
                $"Es sind höchstens {CareerRequirementsConfig.MaxItems} Anforderungen möglich.");
        }
        foreach (var item in items)
        {
            if ((item.Text?.Length ?? 0) > CareerRequirementsConfig.MaxTextLength)
            {
                throw new InvalidOperationException(
                    $"Eine Anforderung darf höchstens {CareerRequirementsConfig.MaxTextLength} Zeichen lang sein.");
            }
            var alternatives = item.Alternatives ?? new List<string>();
            if (alternatives.Count > CareerRequirementsConfig.MaxAlternatives)
            {
                throw new InvalidOperationException(
                    $"Eine Anforderung darf höchstens {CareerRequirementsConfig.MaxAlternatives} ODER-Alternativen haben.");
            }
            if (alternatives.Any(a => (a?.Length ?? 0) > CareerRequirementsConfig.MaxTextLength))
            {
                throw new InvalidOperationException(
                    $"Eine Alternative darf höchstens {CareerRequirementsConfig.MaxTextLength} Zeichen lang sein.");
            }
        }
    }

    private static string Summarise(CareerRequirementsConfig config)
    {
        var items = config.Items ?? new List<CareerRequirement>();
        var alternatives = items.Sum(i => i.Alternatives?.Count ?? 0);
        return $"{items.Count} Anforderungen, {alternatives} Alternativen";
    }
}
