using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AgentInviteService"/> against in-memory SQLite.</summary>
public sealed class AgentInviteServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();

    // RedeemForExistingAsync is the only method touching UserManager; the ctor param
    // is otherwise unused, so null! is safe for every method under test here.
    private AgentInviteService NewService()
        => new(_ctx.Factory, null!);

    private static ClaimsPrincipal Leadership()
        => ClaimsPrincipalBuilder.Agent("a1").WithRank(Rank.Director).WithCodename("Falke").Build();

    private static ClaimsPrincipal NonLeadership()
        => ClaimsPrincipalBuilder.Agent("a2").WithRank(Rank.Trainee).Build();

    private AgentInvite SeedInvite(Action<AgentInvite>? configure = null)
    {
        var invite = new AgentInvite
        {
            Token = Guid.NewGuid().ToString("N"),
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(invite);
        using var db = _ctx.NewContext();
        db.AgentInvites.Add(invite);
        db.SaveChanges();
        return invite;
    }

    // ==================== CreateAsync ====================

    [Fact]
    public async Task CreateAsync_PersistsInvite_WhenLeadership()
    {
        var svc = NewService();
        var expires = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc);

        var result = await svc.CreateAsync("Rekrutierung Q3", expires, Leadership());

        Assert.False(string.IsNullOrWhiteSpace(result.Token));
        using var check = _ctx.NewContext();
        var stored = Assert.Single(await check.AgentInvites.ToListAsync());
        Assert.Equal(result.Id, stored.Id);
        Assert.Equal("Rekrutierung Q3", stored.Label);
        Assert.Equal("Falke", stored.CreatedByName);
        Assert.Equal(expires, stored.ExpiresAt);
        Assert.Null(stored.UsedAt);
        Assert.Null(stored.UsedByUserId);
    }

    [Fact]
    public async Task CreateAsync_TrimsLabel_AndNullsBlankLabel()
    {
        var svc = NewService();

        var trimmed = await svc.CreateAsync("  Team B  ", null, Leadership());
        var blank = await svc.CreateAsync("   ", null, Leadership());

        Assert.Equal("Team B", trimmed.Label);
        Assert.Null(blank.Label);
        Assert.Null(trimmed.ExpiresAt);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenNotLeadership()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("x", null, NonLeadership()));

        using var check = _ctx.NewContext();
        Assert.Empty(await check.AgentInvites.ToListAsync());
    }

    // ==================== ValidateAsync ====================

    [Fact]
    public async Task ValidateAsync_ReturnsInvite_WhenValid()
    {
        var invite = SeedInvite();
        var svc = NewService();

        var result = await svc.ValidateAsync(invite.Token);

        Assert.NotNull(result);
        Assert.Equal(invite.Id, result!.Id);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenTokenBlank()
    {
        var svc = NewService();

        Assert.Null(await svc.ValidateAsync(null));
        Assert.Null(await svc.ValidateAsync("   "));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenAlreadyUsed()
    {
        var invite = SeedInvite(i => i.UsedAt = DateTime.UtcNow.AddDays(-1));
        var svc = NewService();

        Assert.Null(await svc.ValidateAsync(invite.Token));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenExpired()
    {
        var invite = SeedInvite(i => i.ExpiresAt = DateTime.UtcNow.AddDays(-1));
        var svc = NewService();

        Assert.Null(await svc.ValidateAsync(invite.Token));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsNull_WhenTokenNotFound()
    {
        var svc = NewService();

        Assert.Null(await svc.ValidateAsync("does-not-exist"));
    }

    // ==================== ConsumeAsync ====================

    [Fact]
    public async Task ConsumeAsync_MarksUsed_WhenValid()
    {
        var invite = SeedInvite();
        var svc = NewService();
        var before = DateTime.UtcNow;

        await svc.ConsumeAsync(invite.Token, "user-99");

        using var check = _ctx.NewContext();
        var stored = await check.AgentInvites.FirstAsync(i => i.Id == invite.Id);
        Assert.Equal("user-99", stored.UsedByUserId);
        Assert.NotNull(stored.UsedAt);
        Assert.True(stored.UsedAt >= before && stored.UsedAt <= DateTime.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task ConsumeAsync_Throws_WhenAlreadyUsed()
    {
        var invite = SeedInvite(i => i.UsedAt = DateTime.UtcNow.AddDays(-1));
        var svc = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ConsumeAsync(invite.Token, "user-1"));
    }

    [Fact]
    public async Task ConsumeAsync_Throws_WhenTokenNotFound()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ConsumeAsync("missing-token", "user-1"));
    }

    // RedeemForExistingAsync: SKIPPED — depends on UserManager<Agent>
    // (FindByIdAsync / UpdateAsync / UpdateSecurityStampAsync), which is hard to build.

    // ==================== RevokeAsync ====================

    [Fact]
    public async Task RevokeAsync_RemovesInvite_WhenLeadership()
    {
        // No soft-delete interceptor in the test context -> Remove() hard-deletes here.
        var invite = SeedInvite();
        var svc = NewService();

        await svc.RevokeAsync(invite.Id, Leadership());

        using var check = _ctx.NewContext();
        Assert.Empty(await check.AgentInvites.Where(i => i.Id == invite.Id).ToListAsync());
    }

    [Fact]
    public async Task RevokeAsync_NoOp_WhenNotFound()
    {
        var invite = SeedInvite();
        var svc = NewService();

        await svc.RevokeAsync("unknown-id", Leadership());

        using var check = _ctx.NewContext();
        Assert.Single(await check.AgentInvites.ToListAsync());
    }

    [Fact]
    public async Task RevokeAsync_Throws_WhenNotLeadership()
    {
        var invite = SeedInvite();
        var svc = NewService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RevokeAsync(invite.Id, NonLeadership()));

        using var check = _ctx.NewContext();
        Assert.Single(await check.AgentInvites.ToListAsync());
    }

    // ==================== ListAsync ====================

    [Fact]
    public async Task ListAsync_ReturnsNewestFirst_WhenLeadership()
    {
        SeedInvite(i =>
        {
            i.Label = "old";
            i.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        SeedInvite(i =>
        {
            i.Label = "new";
            i.CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        });
        var svc = NewService();

        var result = await svc.ListAsync(Leadership());

        Assert.Equal(2, result.Count);
        Assert.Equal("new", result[0].Label);
        Assert.Equal("old", result[1].Label);
    }

    [Fact]
    public async Task ListAsync_Throws_WhenNotLeadership()
    {
        var svc = NewService();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ListAsync(NonLeadership()));
    }

    public void Dispose() => _ctx.Dispose();
}
