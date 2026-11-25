using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Demo seed fills every area once.</summary>
public sealed class DemoDataSeedTests
{
    private sealed class DemoUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new(DemoIdentity.AgentId, DemoIdentity.Codename, true, false, false);
    }

    // upsert needs MySQL
    private sealed class CountingCaseNumbers : ICaseNumberService
    {
        private int _next;

        public Task<string> NextAsync(AppDbContext db, string prefix, CancellationToken cancellationToken = default)
            => Task.FromResult($"FIB-{prefix}-2026-{++_next:0000}");
    }

    private static ClaimsPrincipal Bootstrap()
    {
        var identity = new ClaimsIdentity("Test");
        identity.AddClaim(new Claim(AgentClaimTypes.IsAdmin, "true"));
        identity.AddClaim(new Claim(AgentClaimTypes.IsBootstrap, "true"));
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, DemoIdentity.AgentId));
        identity.AddClaim(new Claim(AgentClaimTypes.Status, AgentStatus.Active.ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Rank, ((int)Rank.Director).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.Division, ((int)Division.Cid).ToString()));
        identity.AddClaim(new Claim(AgentClaimTypes.IsTRU, "false"));
        return new ClaimsPrincipal(identity);
    }

    private static (DemoDataService Service, TestDbContextFactory Factory, AppDbContext IdentityDb) Build(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new DemoUser()))
            .Options;
        var factory = new TestDbContextFactory(options);
        var identityDb = new AppDbContext(options);
        var users = new UserManager<Agent>(new UserStore<Agent>(identityDb), Options.Create(new IdentityOptions()),
            new PasswordHasher<Agent>(), [], [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), null!, NullLogger<UserManager<Agent>>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var situation = new PublicSituationService(factory, new PublicModuleService(factory, cache), cache);
        return (new DemoDataService(factory, new CountingCaseNumbers(), users, situation), factory, identityDb);
    }

    [Fact]
    public async Task Seeds_every_area_once()
    {
        using var ctx = new SqliteTestContext();
        var (service, factory, identityDb) = Build(ctx);
        await using var _ = identityDb;

        var first = await service.SeedAsync(Bootstrap());
        var second = await service.SeedAsync(Bootstrap());

        Assert.True(first > 300, $"nur {first} Zeilen");
        Assert.Equal(0, second);

        await using var db = factory.CreateDbContext();
        Assert.True(await db.Users.CountAsync(u => u.Rank != null && u.Status == AgentStatus.Active) >= 14);
        Assert.Equal(4, await db.Cases.CountAsync());
        Assert.Equal(3, await db.Operations.CountAsync());
        Assert.Equal(3, await db.Taskforces.CountAsync());
        Assert.True(await db.Jobs.AnyAsync());
        Assert.True(await db.Appointments.AnyAsync());
        Assert.True(await db.Announcements.AnyAsync());
        Assert.True(await db.Documents.AnyAsync());
        Assert.True(await db.Bewerbungen.AnyAsync());
        Assert.True(await db.AgentRankHistories.AnyAsync(h => h.AgentId == "demo-agent-wolf"));

        // colleague as author
        var comment = await db.Comments.SingleAsync(c => c.Text.StartsWith("Lage ist ruhig"));
        Assert.Equal("demo-agent-marder", comment.CreatedById);
        Assert.True(comment.CreatedAt < DateTime.UtcNow.AddDays(-3));
    }
}
