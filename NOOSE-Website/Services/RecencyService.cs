using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.DrugRoutes;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Services;

public class RecencyService(IDbContextFactory<AppDbContext> dbFactory, IMemoryCache cache) : IRecencyService
{
    private const string CacheKey = "aktualitaet:schwellen";

    /// <summary>Supported record types with default thresholds (warning/stale days).</summary>
    private static readonly RecencyTypeInfo[] Types =
    {
        new(nameof(Person), "Person", 30, 90),
        new(nameof(Faction), "Fraktion", 60, 180),
        new(nameof(PersonGroup), "Personengruppe", 60, 180),
        new(nameof(Party), "Partei", 60, 180),
        new(nameof(DrugRoute), "Drogenroute", 60, 180),
        new(nameof(Operation), "Operation", 30, 90),
        new(nameof(Taskforce), "Taskforce", 30, 90),
        new(nameof(Case), "Vorgang", 30, 90),
    };

    public IReadOnlyList<RecencyTypeInfo> SupportedTypes => Types;

    public async Task<IReadOnlyDictionary<string, RecencySettings>> GetAllSettingsAsync(
        CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, RecencySettings>? cached) && cached is not null)
        {
            return cached;
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var overrides = await db.RecencyThresholds
            .ToDictionaryAsync(s => s.RecordsType, s => s, cancellationToken);

        var result = Types.ToDictionary(
            t => t.Type,
            t => overrides.TryGetValue(t.Type, out var o)
                ? new RecencySettings(o.WarningDays, o.StaleDays, o.AgingDisabled)
                : new RecencySettings(t.DefaultWarningDays, t.DefaultStaleDays, false));

        cache.Set(CacheKey, (IReadOnlyDictionary<string, RecencySettings>)result, TimeSpan.FromMinutes(10));
        return result;
    }

    public async Task<RecencySettings> GetSettingsAsync(string recordsType, CancellationToken cancellationToken = default)
    {
        var all = await GetAllSettingsAsync(cancellationToken);
        if (all.TryGetValue(recordsType, out var s))
        {
            return s;
        }
        var @default = Types.FirstOrDefault(t => t.Type == recordsType);
        return @default is not null
            ? new RecencySettings(@default.DefaultWarningDays, @default.DefaultStaleDays, false)
            : new RecencySettings(30, 90, false);
    }

    public async Task<RecencyLevel> AssessAsync(string recordsType, DateTime referenceDate, CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(recordsType, cancellationToken);
        if (settings.AgingDisabled)
        {
            return RecencyLevel.Fresh;
        }
        return RecencyAssessment.Level(settings.WarningDays, settings.StaleDays, referenceDate, DateTime.UtcNow);
    }

    public async Task SaveAsync(string recordsType, int warningDays, int staleDays, bool agingDisabled, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        Permission.RequireLeadership(actor);

        if (Types.All(t => t.Type != recordsType))
        {
            throw new InvalidOperationException($"Unbekannter Aktentyp '{recordsType}'.");
        }
        // clamp: stale never before warning
        warningDays = Math.Max(0, warningDays);
        staleDays = Math.Max(warningDays, staleDays);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var exists = await db.RecencyThresholds.FirstOrDefaultAsync(s => s.RecordsType == recordsType, cancellationToken);
        if (exists is null)
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = recordsType,
                WarningDays = warningDays,
                StaleDays = staleDays,
                AgingDisabled = agingDisabled,
            });
        }
        else
        {
            exists.WarningDays = warningDays;
            exists.StaleDays = staleDays;
            exists.AgingDisabled = agingDisabled;
        }
        await db.SaveChangesAsync(cancellationToken);

        cache.Remove(CacheKey);
    }

    public async Task SetRecordExemptionAsync(string recordsType, string id, bool disabled, ClaimsPrincipal actor,
        CancellationToken cancellationToken = default)
    {
        // ExecuteUpdate bypasses all interceptors, so guard both axes explicitly.
        Permission.RequireLeadership(actor);
        Permission.RequireWriteAccess(actor);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Raw update keeps ModifiedAt untouched; otherwise toggling would reset the very freshness it controls.
        var affected = recordsType switch
        {
            nameof(Person) => await db.People.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(Faction) => await db.Factions.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(PersonGroup) => await db.PersonGroups.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(Party) => await db.Parties.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(DrugRoute) => await db.DrugRoutes.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(Operation) => await db.Operations.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(Taskforce) => await db.Taskforces.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            nameof(Case) => await db.Cases.Where(x => x.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.AgingDisabled, disabled), cancellationToken),
            _ => throw new InvalidOperationException($"Unbekannter Aktentyp '{recordsType}'."),
        };

        if (affected == 0)
        {
            throw new InvalidOperationException("Akte nicht gefunden.");
        }

        // audit manually: the record update above bypassed the interceptor
        db.AuditLogs.Add(ManualAudit.Row(recordsType, id, AuditAction.Modified, actor,
            ManualAudit.Change("Aktualitäts-Ausnahme", null, disabled)));
        await db.SaveChangesAsync(cancellationToken);
    }
}
