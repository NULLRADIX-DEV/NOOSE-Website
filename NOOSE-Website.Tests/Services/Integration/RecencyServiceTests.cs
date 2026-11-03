using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for RecencyService against in-memory SQLite.</summary>
public sealed class RecencyServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private RecencyService NewService() => new(_ctx.Factory, _cache);

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    public void Dispose() => _ctx.Dispose();

    // ---------------------------------------------------------------- SupportedTypes

    [Fact]
    public void SupportedTypes_ExposesAllSevenRecordTypes()
    {
        var svc = NewService();

        var types = svc.SupportedTypes;

        Assert.Equal(7, types.Count);
        Assert.Contains(types, t => t.Type == nameof(Person) && t.DefaultWarningDays == 30 && t.DefaultStaleDays == 90);
        Assert.Contains(types, t => t.Type == nameof(Faction) && t.DefaultWarningDays == 60 && t.DefaultStaleDays == 180);
        Assert.Contains(types, t => t.Type == nameof(Case));
        Assert.Contains(types, t => t.Type == nameof(Taskforce));
    }

    // ---------------------------------------------------------------- GetAllSettingsAsync

    [Fact]
    public async Task GetAllSettingsAsync_ReturnsCodeDefaults_WhenNoOverrides()
    {
        var svc = NewService();

        var all = await svc.GetAllSettingsAsync();

        Assert.Equal(7, all.Count);
        var person = all[nameof(Person)];
        Assert.Equal(30, person.WarningDays);
        Assert.Equal(90, person.StaleDays);
        Assert.False(person.AgingDisabled);
    }

    [Fact]
    public async Task GetAllSettingsAsync_AppliesStoredOverrides()
    {
        using (var db = _ctx.NewContext())
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = nameof(Person),
                WarningDays = 7,
                StaleDays = 14,
                AgingDisabled = true,
            });
            db.SaveChanges();
        }
        var svc = NewService();

        var person = (await svc.GetAllSettingsAsync())[nameof(Person)];

        Assert.Equal(7, person.WarningDays);
        Assert.Equal(14, person.StaleDays);
        Assert.True(person.AgingDisabled);
    }

    [Fact]
    public async Task GetAllSettingsAsync_ServesCachedResult_IgnoringLaterDbChanges()
    {
        var svc = NewService();
        var first = (await svc.GetAllSettingsAsync())[nameof(Person)];
        Assert.Equal(30, first.WarningDays); // populated cache with defaults

        // mutate the DB behind the cache
        using (var db = _ctx.NewContext())
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = nameof(Person),
                WarningDays = 999,
                StaleDays = 1000,
            });
            db.SaveChanges();
        }

        var second = (await svc.GetAllSettingsAsync())[nameof(Person)];
        Assert.Equal(30, second.WarningDays); // still the cached value
    }

    // ---------------------------------------------------------------- GetSettingsAsync

    [Fact]
    public async Task GetSettingsAsync_ReturnsDefault_ForKnownTypeWithoutOverride()
    {
        var svc = NewService();

        var s = await svc.GetSettingsAsync(nameof(Faction));

        Assert.Equal(60, s.WarningDays);
        Assert.Equal(180, s.StaleDays);
        Assert.False(s.AgingDisabled);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsOverride_WhenStored()
    {
        using (var db = _ctx.NewContext())
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = nameof(Faction),
                WarningDays = 5,
                StaleDays = 9,
            });
            db.SaveChanges();
        }
        var svc = NewService();

        var s = await svc.GetSettingsAsync(nameof(Faction));

        Assert.Equal(5, s.WarningDays);
        Assert.Equal(9, s.StaleDays);
    }

    [Fact]
    public async Task GetSettingsAsync_ReturnsGenericFallback_ForUnknownType()
    {
        var svc = NewService();

        var s = await svc.GetSettingsAsync("Bogus");

        Assert.Equal(30, s.WarningDays);
        Assert.Equal(90, s.StaleDays);
        Assert.False(s.AgingDisabled);
    }

    // ---------------------------------------------------------------- AssessAsync

    [Fact]
    public async Task AssessAsync_ReturnsFresh_WhenWithinWarningWindow()
    {
        var svc = NewService();

        var level = await svc.AssessAsync(nameof(Person), DateTime.UtcNow.AddDays(-5));

        Assert.Equal(RecencyLevel.Fresh, level);
    }

    [Fact]
    public async Task AssessAsync_ReturnsWarning_WhenPastWarningButNotStale()
    {
        var svc = NewService();

        var level = await svc.AssessAsync(nameof(Person), DateTime.UtcNow.AddDays(-45));

        Assert.Equal(RecencyLevel.Warning, level);
    }

    [Fact]
    public async Task AssessAsync_ReturnsStale_WhenPastStaleWindow()
    {
        var svc = NewService();

        var level = await svc.AssessAsync(nameof(Person), DateTime.UtcNow.AddDays(-120));

        Assert.Equal(RecencyLevel.Stale, level);
    }

    [Fact]
    public async Task AssessAsync_ReturnsFresh_WhenTypeAgingDisabled()
    {
        using (var db = _ctx.NewContext())
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = nameof(Person),
                WarningDays = 30,
                StaleDays = 90,
                AgingDisabled = true,
            });
            db.SaveChanges();
        }
        var svc = NewService();

        // 200 days old, yet aging is off for the whole type -> Fresh
        var level = await svc.AssessAsync(nameof(Person), DateTime.UtcNow.AddDays(-200));

        Assert.Equal(RecencyLevel.Fresh, level);
    }

    // ---------------------------------------------------------------- SaveAsync

    [Fact]
    public async Task SaveAsync_InsertsThreshold_WhenNoneExists()
    {
        var svc = NewService();

        await svc.SaveAsync(nameof(Person), 12, 40, agingDisabled: true, Leader());

        using var db = _ctx.NewContext();
        var row = await db.RecencyThresholds.SingleAsync(t => t.RecordsType == nameof(Person));
        Assert.Equal(12, row.WarningDays);
        Assert.Equal(40, row.StaleDays);
        Assert.True(row.AgingDisabled);
    }

    [Fact]
    public async Task SaveAsync_UpdatesThreshold_WhenAlreadyExists()
    {
        using (var db = _ctx.NewContext())
        {
            db.RecencyThresholds.Add(new RecencyThreshold
            {
                RecordsType = nameof(Person),
                WarningDays = 1,
                StaleDays = 2,
            });
            db.SaveChanges();
        }
        var svc = NewService();

        await svc.SaveAsync(nameof(Person), 20, 50, agingDisabled: false, Leader());

        using var check = _ctx.NewContext();
        var rows = await check.RecencyThresholds.Where(t => t.RecordsType == nameof(Person)).ToListAsync();
        Assert.Single(rows);
        Assert.Equal(20, rows[0].WarningDays);
        Assert.Equal(50, rows[0].StaleDays);
    }

    [Fact]
    public async Task SaveAsync_ClampsNegativeWarningAndStaleBelowWarning()
    {
        var svc = NewService();

        // warning < 0 -> 0 ; stale (10) < warning (50) -> raised to warning
        await svc.SaveAsync(nameof(Person), -5, 10, agingDisabled: false, Leader());
        await svc.SaveAsync(nameof(Faction), 50, 10, agingDisabled: false, Leader());

        using var db = _ctx.NewContext();
        var person = await db.RecencyThresholds.SingleAsync(t => t.RecordsType == nameof(Person));
        Assert.Equal(0, person.WarningDays);
        Assert.Equal(10, person.StaleDays);

        var faction = await db.RecencyThresholds.SingleAsync(t => t.RecordsType == nameof(Faction));
        Assert.Equal(50, faction.WarningDays);
        Assert.Equal(50, faction.StaleDays); // stale clamped up to warning
    }

    [Fact]
    public async Task SaveAsync_ClearsCache_SoNextReadSeesNewValue()
    {
        var svc = NewService();
        var before = await svc.GetSettingsAsync(nameof(Person)); // seeds cache with defaults
        Assert.Equal(30, before.WarningDays);

        await svc.SaveAsync(nameof(Person), 3, 6, agingDisabled: false, Leader());

        var after = await svc.GetSettingsAsync(nameof(Person));
        Assert.Equal(3, after.WarningDays);
        Assert.Equal(6, after.StaleDays);
    }

    [Fact]
    public async Task SaveAsync_Throws_ForUnknownRecordType()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SaveAsync("Bogus", 10, 20, agingDisabled: false, Leader()));
    }

    [Fact]
    public async Task SaveAsync_ThrowsUnauthorized_WhenActorNotLeadership()
    {
        var svc = NewService();
        var junior = ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.Trainee).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SaveAsync(nameof(Person), 10, 20, agingDisabled: false, junior));

        using var db = _ctx.NewContext();
        Assert.False(await db.RecencyThresholds.AnyAsync());
    }

    // ---------------------------------------------------------------- SetRecordExemptionAsync

    [Theory]
    [InlineData(nameof(Person))]
    [InlineData(nameof(Faction))]
    [InlineData(nameof(PersonGroup))]
    [InlineData(nameof(Party))]
    [InlineData(nameof(Operation))]
    [InlineData(nameof(Taskforce))]
    [InlineData(nameof(Case))]
    public async Task SetRecordExemptionAsync_TogglesAgingFlag_OnEachRecordType(string recordsType)
    {
        const string id = "rec-1";
        using (var db = _ctx.NewContext())
        {
            SeedRecord(db, recordsType, id);
            db.SaveChanges();
        }
        var svc = NewService();

        await svc.SetRecordExemptionAsync(recordsType, id, disabled: true, Leader());

        using var check = _ctx.NewContext();
        Assert.True(await ReadAgingDisabledAsync(check, recordsType, id));
    }

    [Fact]
    public async Task SetRecordExemptionAsync_ThrowsUnauthorized_WhenActorNotLeadership()
    {
        const string id = "p-1";
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person(id));
            db.SaveChanges();
        }
        var svc = NewService();
        var junior = ClaimsPrincipalBuilder.Agent("j").WithRank(Rank.Trainee).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetRecordExemptionAsync(nameof(Person), id, disabled: true, junior));

        using var check = _ctx.NewContext();
        Assert.False((await check.People.FirstAsync(x => x.Id == id)).AgingDisabled);
    }

    [Fact]
    public async Task SetRecordExemptionAsync_ThrowsUnauthorized_WhenActorIsOnlyReader()
    {
        const string id = "p-1";
        using (var db = _ctx.NewContext())
        {
            db.People.Add(Seed.Person(id));
            db.SaveChanges();
        }
        var svc = NewService();
        // leadership rank + team lead (not admin) -> passes RequireLeadership, fails RequireWriteAccess
        var onlyReader = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.SetRecordExemptionAsync(nameof(Person), id, disabled: true, onlyReader));
    }

    [Fact]
    public async Task SetRecordExemptionAsync_Throws_WhenRecordMissing()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetRecordExemptionAsync(nameof(Person), "does-not-exist", disabled: true, Leader()));
    }

    [Fact]
    public async Task SetRecordExemptionAsync_Throws_ForUnknownRecordType()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetRecordExemptionAsync("Bogus", "id", disabled: true, Leader()));
    }

    // ---------------------------------------------------------------- helpers

    private static void SeedRecord(AppDbContext db, string type, string id)
    {
        switch (type)
        {
            case nameof(Person): db.People.Add(Seed.Person(id)); break;
            case nameof(Faction): db.Factions.Add(Seed.Faction(id)); break;
            case nameof(PersonGroup): db.PersonGroups.Add(new PersonGroup { Id = id, Name = "Gruppe" }); break;
            case nameof(Party): db.Parties.Add(new Party { Id = id, Name = "Partei" }); break;
            case nameof(Operation): db.Operations.Add(new Operation { Id = id, Title = "Operation" }); break;
            case nameof(Taskforce): db.Taskforces.Add(new Taskforce { Id = id, Name = "Taskforce" }); break;
            case nameof(Case): db.Cases.Add(Seed.Case(id)); break;
            default: throw new ArgumentOutOfRangeException(nameof(type), type, null);
        }
    }

    private static async Task<bool> ReadAgingDisabledAsync(AppDbContext db, string type, string id) => type switch
    {
        nameof(Person) => (await db.People.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(Faction) => (await db.Factions.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(PersonGroup) => (await db.PersonGroups.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(Party) => (await db.Parties.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(Operation) => (await db.Operations.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(Taskforce) => (await db.Taskforces.FirstAsync(x => x.Id == id)).AgingDisabled,
        nameof(Case) => (await db.Cases.FirstAsync(x => x.Id == id)).AgingDisabled,
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
    };
}
