using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>Per-rank NOOSEI quota rules and anomaly thresholds. Only the AI owner may change them.</summary>
public interface ILlmQuotaConfigService
{
    /// <summary>Cached rules for read paths.</summary>
    Task<LlmQuotaConfig> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>Always fresh; the editor must never show a stale cache.</summary>
    Task<LlmQuotaConfig> GetEditableAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LlmQuotaConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="ILlmQuotaConfigService" />
public class LlmQuotaConfigService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : ILlmQuotaConfigService
{
    private const string SettingKey = "KiKontingente";
    private const string CacheKey = "ki:kontingente";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Audit entity type for the config row; SystemSetting is not auditable on its own.</summary>
    public const string AuditType = "LlmQuotaConfig";

    public async Task<LlmQuotaConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out LlmQuotaConfig? cached) && cached is not null)
        {
            return cached;
        }
        var config = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public Task<LlmQuotaConfig> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken); // always fresh

    private async Task<LlmQuotaConfig> LoadAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var raw = (await db.SystemSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return LlmQuotaConfig.Default();
        }
        try
        {
            var config = JsonSerializer.Deserialize<LlmQuotaConfig>(raw) ?? LlmQuotaConfig.Default();
            // a stored config that violates the invariants would hand out impossible quotas → fall back
            Validate(config);
            return config;
        }
        catch (JsonException)
        {
            return LlmQuotaConfig.Default();
        }
        catch (InvalidOperationException)
        {
            return LlmQuotaConfig.Default();
        }
    }

    public async Task SaveAsync(LlmQuotaConfig config, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
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
            ManualAudit.Change("KI-Kontingente", null, Summarise(config))));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Validate config invariants.</summary>
    public static void Validate(LlmQuotaConfig config)
    {
        foreach (var rank in RankDisplay.All)
        {
            if (!config.Ranks.TryGetValue(LlmQuotaConfig.RankKey(rank), out var quota))
            {
                continue;
            }
            if (quota.BaseWeekly < 0)
            {
                throw new InvalidOperationException(
                    $"Das Wochenkontingent für {RankDisplay.Name(rank)} darf nicht negativ sein.");
            }
            if (quota.CarryOverPercent is < 0 or > 100)
            {
                throw new InvalidOperationException(
                    $"Der Übertrag für {RankDisplay.Name(rank)} muss zwischen 0 und 100 % liegen.");
            }
        }

        foreach (var agency in PartnerAgencyDisplay.All)
        {
            if (!config.PartnerAgencies.TryGetValue(LlmQuotaConfig.AgencyKey(agency), out var quota))
            {
                continue;
            }
            if (quota.BaseWeekly < 0)
            {
                throw new InvalidOperationException(
                    $"Das Wochenkontingent für {PartnerAgencyDisplay.Name(agency)} darf nicht negativ sein.");
            }
            if (quota.CarryOverPercent is < 0 or > 100)
            {
                throw new InvalidOperationException(
                    $"Der Übertrag für {PartnerAgencyDisplay.Name(agency)} muss zwischen 0 und 100 % liegen.");
            }
        }

        var t = config.Anomalies;
        Range(t.SpikeFactor >= 1, "Der Faktor für Kostenausreißer muss mindestens 1 sein.");
        Range(t.SpikeBaselineDays is >= 1 and <= 90, "Der Bezugszeitraum für Kostenausreißer muss zwischen 1 und 90 Tagen liegen.");
        Range(t.SpikeMinBaselineCount >= 1, "Die Mindestzahl an Vergleichsanfragen muss mindestens 1 sein.");
        Range(t.SpikeMinTokens >= 0, "Die Token-Untergrenze für Kostenausreißer darf nicht negativ sein.");
        Range(t.BurnPercent is >= 1 and <= 100, "Der Verbrauchsanteil muss zwischen 1 und 100 % liegen.");
        Range(t.BurnHours is >= 1 and <= 168, "Das Verbrauchsfenster muss zwischen 1 und 168 Stunden liegen.");
        Range(t.BurstMinutes is >= 1 and <= 1440, "Das Serien-Fenster muss zwischen 1 und 1440 Minuten liegen.");
        Range(t.BurstRequests >= 2, "Eine Anfrage-Serie braucht mindestens 2 Anfragen.");
        Range(t.BurstDuplicates >= 2, "Eine Anfrage-Serie braucht mindestens 2 ähnliche Anfragen.");
        Range(t.BurstSimilarityPercent is >= 50 and <= 100, "Die Ähnlichkeitsschwelle muss zwischen 50 und 100 % liegen.");
        Range(t.OutlierOwnFactor >= 1, "Der Faktor gegenüber dem eigenen Schnitt muss mindestens 1 sein.");
        Range(t.OutlierRankFactor >= 1, "Der Faktor gegenüber dem Rang-Schnitt muss mindestens 1 sein.");
        Range(t.OutlierTrailingWeeks is >= 1 and <= 52, "Der Vergleichszeitraum muss zwischen 1 und 52 Wochen liegen.");
        Range(t.OutlierMinWeeks >= 1 && t.OutlierMinWeeks <= t.OutlierTrailingWeeks,
            "Die Mindestzahl an Vergleichswochen muss zwischen 1 und dem Vergleichszeitraum liegen.");
    }

    private static void Range(bool ok, string message)
    {
        if (!ok)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string Summarise(LlmQuotaConfig config)
        => string.Join(" · ", RankDisplay.All.Select(r =>
        {
            var quota = config.For(r);
            return $"{RankDisplay.Name(r)}: {quota.BaseWeekly:N0} / {quota.CarryOverPercent} %";
        }).Concat(PartnerAgencyDisplay.All
            .Where(a => config.PartnerAgencies.ContainsKey(LlmQuotaConfig.AgencyKey(a)))
            .Select(a =>
            {
                var quota = config.For(null, a);
                return $"{PartnerAgencyDisplay.Name(a)}: {quota.BaseWeekly:N0} / {quota.CarryOverPercent} %";
            })));
}
