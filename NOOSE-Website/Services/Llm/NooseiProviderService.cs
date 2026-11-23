using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Llm;

namespace NOOSE_Website.Services;

/// <summary>Which upstream NOOSEI talks to, and what that upstream does to the weekly quota. Only the AI owner
/// may change either — they pay the bill, and the boost is the lever that turns a cheaper endpoint into more
/// questions per agent rather than into a smaller invoice.</summary>
public interface INooseiProviderService
{
    /// <summary>Resolved state for read paths: the upstream a request takes right now and its boost.</summary>
    Task<LlmProviderState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Always fresh, unresolved; the editor must never show a stale cache or a fallback as a choice.</summary>
    Task<LlmProviderSettings> GetEditableAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(LlmProviderSettings settings, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiProviderService" />
public class NooseiProviderService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache,
    IOptions<LlmOptions> options) : INooseiProviderService
{
    public const string SettingKey = "KiAnbieter";
    private const string CacheKey = "ki:anbieter";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    /// <summary>Audit entity type for the config row; SystemSetting is not auditable on its own.</summary>
    public const string AuditType = "LlmProviderSettings";

    private readonly LlmOptions _o = options.Value;

    public async Task<LlmProviderState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var settings = await CachedAsync(cancellationToken);
        // resolve here, not at the switch: a key pulled out of the environment must not fail every next question
        var active = _o.Resolve(settings.Active);
        return new LlmProviderState(active, settings.BoostFor(active));
    }

    public Task<LlmProviderSettings> GetEditableAsync(CancellationToken cancellationToken = default)
        => LoadAsync(cancellationToken); // always fresh

    private async Task<LlmProviderSettings> CachedAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(CacheKey, out LlmProviderSettings? cached) && cached is not null)
        {
            return cached;
        }
        var settings = await LoadAsync(cancellationToken);
        cache.Set(CacheKey, settings, CacheDuration);
        return settings;
    }

    private async Task<LlmProviderSettings> LoadAsync(CancellationToken cancellationToken)
    {
        string? raw;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            raw = (await db.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(e => e.Key == SettingKey, cancellationToken))?.Value;
        }
        catch (Exception)
        {
            return new LlmProviderSettings(); // DB down → the deployment's own default, never a failed chat
        }
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new LlmProviderSettings();
        }
        try
        {
            return JsonSerializer.Deserialize<LlmProviderSettings>(raw) ?? new LlmProviderSettings();
        }
        catch (JsonException)
        {
            return new LlmProviderSettings();
        }
    }

    public async Task SaveAsync(LlmProviderSettings settings, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
        Permission.RequireWriteAccess(actor);
        Validate(settings, _o);

        var json = JsonSerializer.Serialize(settings);

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
        db.AuditLogs.Add(ManualAudit.Row(AuditType, "global", AuditAction.Modified, actor,
            ManualAudit.Change("KI-Anbieter", null, Summarise(settings, _o))));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }

    /// <summary>Validate the stored choice against what the deployment can actually reach.</summary>
    public static void Validate(LlmProviderSettings settings, LlmOptions options)
    {
        if (settings.Active is { } active && !options.IsConfiguredFor(active))
        {
            throw new InvalidOperationException(
                $"Für {LlmProviderDisplay.Name(active)} sind Schlüssel und Modell nicht hinterlegt — "
                + "die kommen aus den User Secrets bzw. den Umgebungsvariablen, nicht aus der Datenbank.");
        }
        // the raw dictionary, not BoostFor: that one clamps, and a clamping read would let a bad value be stored
        foreach (var provider in LlmProviderDisplay.All)
        {
            if (settings.BoostPercent.TryGetValue(LlmProviderSettings.ProviderKey(provider), out var percent)
                && percent is < 0 or > LlmProviderSettings.MaxBoostPercent)
            {
                throw new InvalidOperationException(
                    $"Der Kontingent-Aufschlag für {LlmProviderDisplay.Name(provider)} muss zwischen 0 und "
                    + $"{LlmProviderSettings.MaxBoostPercent} % liegen.");
            }
        }
    }

    private static string Summarise(LlmProviderSettings settings, LlmOptions options)
    {
        var active = LlmProviderDisplay.Name(options.Resolve(settings.Active));
        var boosts = string.Join(" · ", LlmProviderDisplay.All
            .Select(p => $"{LlmProviderDisplay.Name(p)}: +{settings.BoostFor(p)} %"));
        return $"Aktiv: {active} · {boosts}";
    }
}
