using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Services;

/// <inheritdoc cref="IRecruitingAutomationService" />
public class RecruitingAutomationService(
    IDbContextFactory<AppDbContext> dbFactory,
    IMemoryCache cache) : IRecruitingAutomationService
{
    public const string EnabledKey = "Recruiting:AutoCase:Enabled";
    public const string TemplateKey = "Recruiting:AutoCase:TemplateId";
    private const string CacheKey = "RecruitingAutomationConfig";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public async Task<RecruitingAutomationConfig> GetAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out RecruitingAutomationConfig? config) && config is not null)
        {
            return config;
        }

        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var values = await db.SystemSettings
                .Where(e => e.Key == EnabledKey || e.Key == TemplateKey)
                .ToDictionaryAsync(e => e.Key, e => e.Value, cancellationToken);

            // missing/anything-but-"false" stays enabled, so it works out of the box
            var enabled = !string.Equals(values.GetValueOrDefault(EnabledKey), "false", StringComparison.OrdinalIgnoreCase);
            var stored = values.GetValueOrDefault(TemplateKey);
            var templateId = string.IsNullOrWhiteSpace(stored) ? ApplicationTemplates.SecurityCheckId : stored;
            config = new RecruitingAutomationConfig(enabled, templateId);
        }
        catch (Exception)
        {
            return new RecruitingAutomationConfig(true, ApplicationTemplates.SecurityCheckId);
        }

        cache.Set(CacheKey, config, CacheDuration);
        return config;
    }

    public async Task SaveAsync(RecruitingAutomationConfig input, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireHrbOrLeadership(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await SetAsync(db, EnabledKey, input.AutoCaseEnabled ? "true" : "false", cancellationToken);
        await SetAsync(db, TemplateKey, string.IsNullOrWhiteSpace(input.TemplateId) ? null : input.TemplateId.Trim(), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        cache.Remove(CacheKey);
    }

    private static async Task SetAsync(AppDbContext db, string key, string? value, CancellationToken cancellationToken)
    {
        var row = await db.SystemSettings.FirstOrDefaultAsync(e => e.Key == key, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = key, Value = value });
        }
        else
        {
            row.Value = value;
        }
    }
}
