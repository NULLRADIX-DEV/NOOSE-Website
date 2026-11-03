using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="BewerbungssperreService"/> against in-memory SQLite.</summary>
public sealed class BewerbungssperreServiceTests
{
    private static BewerbungssperreService CreateService(SqliteTestContext ctx) => new(ctx.Factory);

    // HRB actor passes both RequireHrbOrLeadership and RequireWriteAccess.
    private static System.Security.Claims.ClaimsPrincipal HrbWriter()
        => ClaimsPrincipalBuilder.Agent("hrb").AsHrb().WithCodename("Warden").Build();

    // Junior agent, no flags: fails RequireHrbOrLeadership.
    private static System.Security.Claims.ClaimsPrincipal Unauthorized()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    // Leadership-by-rank team lead (not admin) = OnlyReader: passes HrbOrLeadership, fails RequireWriteAccess.
    private static System.Security.Claims.ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("reader").WithRank(Rank.Director).AsTeamLead().Build();

    private static Bewerbungssperre SeedSperre(SqliteTestContext ctx, string agentId, bool isBlacklist,
        DateTime? bannedUntil, string? reason = null, DateTime? createdAt = null,
        Action<Bewerbungssperre>? configure = null)
    {
        var s = new Bewerbungssperre
        {
            AgentId = agentId,
            IsBlacklist = isBlacklist,
            BannedUntil = bannedUntil,
            Reason = reason,
            CreatedAt = createdAt ?? DateTime.UtcNow,
        };
        configure?.Invoke(s);
        using var db = ctx.NewContext();
        db.Bewerbungssperren.Add(s);
        db.SaveChanges();
        return s;
    }

    // ---- GetActiveAsync ----

    [Fact]
    public async Task GetActiveAsync_ReturnsActiveTempBan()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(5),
            reason: "Grund");
        var service = CreateService(ctx);

        var info = await service.GetActiveAsync("app");

        Assert.NotNull(info);
        Assert.Equal(seeded.Id, info!.Id);
        Assert.False(info.IsBlacklist);
        Assert.Equal("Grund", info.Reason);
    }

    [Fact]
    public async Task GetActiveAsync_BlacklistTakesPrecedenceOverTempBan()
    {
        using var ctx = new SqliteTestContext();
        SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(5));
        var blacklist = SeedSperre(ctx, "app", isBlacklist: true, bannedUntil: null);
        var service = CreateService(ctx);

        var info = await service.GetActiveAsync("app");

        Assert.NotNull(info);
        Assert.Equal(blacklist.Id, info!.Id);
        Assert.True(info.IsBlacklist);
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsNull_WhenAgentIdEmpty()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        Assert.Null(await service.GetActiveAsync(""));
    }

    [Fact]
    public async Task GetActiveAsync_ReturnsNull_WhenTempBanExpired()
    {
        using var ctx = new SqliteTestContext();
        SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddHours(-1));
        var service = CreateService(ctx);

        Assert.Null(await service.GetActiveAsync("app"));
    }

    // ---- ListActiveAsync ----

    [Fact]
    public async Task ListActiveAsync_ReturnsActiveEntries_NewestFirst_ExcludingExpired()
    {
        using var ctx = new SqliteTestContext();
        var now = DateTime.UtcNow;
        SeedSperre(ctx, "a", isBlacklist: false, bannedUntil: now.AddDays(5), createdAt: now.AddDays(-2));
        SeedSperre(ctx, "b", isBlacklist: false, bannedUntil: now.AddDays(5), createdAt: now.AddDays(-1));
        SeedSperre(ctx, "c", isBlacklist: false, bannedUntil: now.AddDays(-1), createdAt: now.AddDays(-3)); // expired
        SeedSperre(ctx, "d", isBlacklist: true, bannedUntil: null, createdAt: now);
        var service = CreateService(ctx);

        var rows = await service.ListActiveAsync(HrbWriter());

        Assert.Equal(3, rows.Count);
        Assert.Equal("d", rows[0].AgentId); // newest CreatedAt first
        Assert.Equal("b", rows[1].AgentId);
        Assert.Equal("a", rows[2].AgentId);
    }

    [Fact]
    public async Task ListActiveAsync_Throws_WhenActorNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListActiveAsync(Unauthorized()));
    }

    // ---- BanAsync ----

    [Fact]
    public async Task BanAsync_CreatesTempBan_WithFieldsAndDiscordId()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("applicant-1"));
            db.SaveChanges();
        }
        var service = CreateService(ctx);

        await service.BanAsync("applicant-1", "b1", "  Max Mustermann  ", "  Regelverstoß  ", HrbWriter());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList());
        Assert.Equal("applicant-1", row.AgentId);
        Assert.False(row.IsBlacklist);
        Assert.Equal("b1", row.BewerbungId);
        Assert.Equal("Max Mustermann", row.ApplicantName);
        Assert.Equal("Regelverstoß", row.Reason);
        Assert.Equal("Warden", row.CreatedByName);
        Assert.Equal("discord-applicant-1", row.DiscordId);
        Assert.NotNull(row.BannedUntil);
        // 14 days spelled out, not BanDuration: a test that reads the constant cannot catch a wrong constant
        Assert.Equal(DateTime.UtcNow.AddDays(14), row.BannedUntil!.Value, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task BanAsync_RefreshesExistingTempBan_WithoutAddingRow()
    {
        using var ctx = new SqliteTestContext();
        var existing = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(2),
            reason: "alt");
        var service = CreateService(ctx);

        await service.BanAsync("app", null, null, "neu", HrbWriter());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList());
        Assert.Equal(existing.Id, row.Id);
        Assert.Equal("neu", row.Reason);
        Assert.Equal(DateTime.UtcNow.AddDays(14), row.BannedUntil!.Value, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task BanAsync_NoOp_WhenBlacklistAlreadyActive()
    {
        using var ctx = new SqliteTestContext();
        var blacklist = SeedSperre(ctx, "app", isBlacklist: true, bannedUntil: null);
        var service = CreateService(ctx);

        await service.BanAsync("app", null, null, "Grund", HrbWriter());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList());
        Assert.Equal(blacklist.Id, row.Id);
        Assert.True(row.IsBlacklist);
    }

    [Fact]
    public async Task BanAsync_NoOp_WhenAgentIdEmpty()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await service.BanAsync("", null, null, "Grund", HrbWriter());

        using var check = ctx.NewContext();
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Fact]
    public async Task BanAsync_Throws_WhenActorNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.BanAsync("app", null, null, "Grund", Unauthorized()));
    }

    [Fact]
    public async Task BanAsync_Throws_WhenActorIsOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        // OnlyReader passes HrbOrLeadership (leadership by rank) but fails RequireWriteAccess.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.BanAsync("app", null, null, "Grund", OnlyReader()));
    }

    // ---- BlacklistAsync ----

    [Fact]
    public async Task BlacklistAsync_CreatesBlacklist_SupersedingTempBan()
    {
        using var ctx = new SqliteTestContext();
        SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(5));
        var service = CreateService(ctx);

        await service.BlacklistAsync("app", "b9", "Max", "endgültig", HrbWriter());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList()); // temp ban superseded (hard-deleted here)
        Assert.True(row.IsBlacklist);
        Assert.Null(row.BannedUntil);
        Assert.Equal("endgültig", row.Reason);
        Assert.Equal("Warden", row.CreatedByName);
    }

    [Fact]
    public async Task BlacklistAsync_NoOp_WhenBlacklistAlreadyActive()
    {
        using var ctx = new SqliteTestContext();
        var existing = SeedSperre(ctx, "app", isBlacklist: true, bannedUntil: null, reason: "erste");
        var service = CreateService(ctx);

        await service.BlacklistAsync("app", null, null, "zweite", HrbWriter());

        using var check = ctx.NewContext();
        var row = Assert.Single(check.Bewerbungssperren.ToList());
        Assert.Equal(existing.Id, row.Id);
        Assert.Equal("erste", row.Reason); // unchanged
    }

    [Fact]
    public async Task BlacklistAsync_Throws_WhenActorNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.BlacklistAsync("app", null, null, "Grund", Unauthorized()));
    }

    // ---- ShortenAsync ----

    [Fact]
    public async Task ShortenAsync_ChangesBannedUntil()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(10));
        var newUntil = DateTime.UtcNow.AddDays(1);
        var service = CreateService(ctx);

        await service.ShortenAsync(seeded.Id, newUntil, HrbWriter());

        using var check = ctx.NewContext();
        var row = check.Bewerbungssperren.Single(s => s.Id == seeded.Id);
        Assert.Equal(newUntil, row.BannedUntil!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ShortenAsync_Throws_WhenBlacklistEntry()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: true, bannedUntil: null);
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ShortenAsync(seeded.Id, DateTime.UtcNow.AddDays(1), HrbWriter()));
    }

    [Fact]
    public async Task ShortenAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ShortenAsync("missing", DateTime.UtcNow.AddDays(1), HrbWriter()));
    }

    [Fact]
    public async Task ShortenAsync_Throws_WhenActorNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(10));
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ShortenAsync(seeded.Id, DateTime.UtcNow.AddDays(1), Unauthorized()));
    }

    // ---- LiftAsync ----

    [Fact]
    public async Task LiftAsync_RemovesRow()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(5));
        var service = CreateService(ctx);

        await service.LiftAsync(seeded.Id, HrbWriter());

        using var check = ctx.NewContext();
        // No soft-delete interceptor in the test context -> Remove hard-deletes; row is gone.
        Assert.Empty(check.Bewerbungssperren.ToList());
    }

    [Fact]
    public async Task LiftAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.LiftAsync("missing", HrbWriter()));
    }

    [Fact]
    public async Task LiftAsync_Throws_WhenActorNotHrbOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var seeded = SeedSperre(ctx, "app", isBlacklist: false, bannedUntil: DateTime.UtcNow.AddDays(5));
        var service = CreateService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.LiftAsync(seeded.Id, Unauthorized()));
    }
}
