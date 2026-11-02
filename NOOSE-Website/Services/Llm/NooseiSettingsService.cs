using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

/// <summary>The one operator-editable piece of NOOSEI's prompt: a house-style addendum appended behind the fixed
/// rules. It can add a convention but never delete a control, which is why the rest of the catalogue stays in code.</summary>
public interface INooseiSettingsService
{
    Task<string?> GetAddendumAsync(CancellationToken cancellationToken = default);

    Task SaveAddendumAsync(string? addendum, ClaimsPrincipal actor, CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="INooseiSettingsService" />
public sealed class NooseiSettingsService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache)
    : INooseiSettingsService
{
    private const string CacheKey = "ki:zusatzhinweis";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(10);

    public const string AuditType = "NooseiPromptAddendum";

    public async Task<string?> GetAddendumAsync(CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out string? cached))
        {
            return cached;
        }
        string? value = null;
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            value = (await db.SystemSettings.AsNoTracking()
                .FirstOrDefaultAsync(s => s.Key == NooseiPrompts.AddendumKey, cancellationToken))?.Value;
        }
        catch (Exception)
        {
            return null; // DB down → no addendum, never a failed chat
        }
        value = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        cache.Set(CacheKey, value, CacheDuration);
        return value;
    }

    public async Task SaveAddendumAsync(string? addendum, ClaimsPrincipal actor, CancellationToken cancellationToken = default)
    {
        Permission.RequireAiOwner(actor);
        Permission.RequireWriteAccess(actor);

        var value = string.IsNullOrWhiteSpace(addendum) ? null : addendum.Trim();
        if (value is { Length: > NooseiPrompts.MaxAddendumChars })
        {
            throw new InvalidOperationException(
                $"Der Zusatzhinweis darf höchstens {NooseiPrompts.MaxAddendumChars} Zeichen lang sein.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.SystemSettings.FirstOrDefaultAsync(s => s.Key == NooseiPrompts.AddendumKey, cancellationToken);
        if (row is null)
        {
            db.SystemSettings.Add(new SystemSetting { Key = NooseiPrompts.AddendumKey, Value = value });
        }
        else
        {
            row.Value = value;
        }
        db.AuditLogs.Add(ManualAudit.Row(AuditType, "global", AuditAction.Modified, actor,
            ManualAudit.Change("FIBI-Zusatzhinweis", null, value ?? "(entfernt)")));
        await db.SaveChangesAsync(cancellationToken);
        cache.Remove(CacheKey);
    }
}
