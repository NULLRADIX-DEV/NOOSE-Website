using System.Security.Claims;
using NOOSE_Website.Data.Entities.Kasse;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Kasse;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="KassenTemplateService"/> over in-memory SQLite.</summary>
public sealed class KassenTemplateServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static KassenTemplateService Build(SqliteTestContext ctx) => new(ctx.Factory);

    private static KassenVorlageInput Input(string name, KassenBuchungArt kind = KassenBuchungArt.Auszahlung, decimal amount = 5000, bool active = true)
        => new() { Name = name, Account = KassenKonto.Schwarzgeld, Kind = kind, Amount = amount, IsActive = active };

    [Fact]
    public async Task Create_Persists_AndAppearsInActive()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("FIB-Ankündigung"), Leader());

        var active = await svc.GetActiveAsync();
        Assert.Single(active);
        Assert.Equal("FIB-Ankündigung", active[0].Name);
    }

    [Fact]
    public async Task Create_RejectsKorrektur()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input("Abgleich", KassenBuchungArt.Korrektur), Leader()));
    }

    [Fact]
    public async Task Create_RejectsDuplicateName()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Miete"), Leader());
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("Miete"), Leader()));
    }

    [Fact]
    public async Task Create_RejectsZeroAmount()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(Input("Leer", amount: 0), Leader()));
    }

    [Fact]
    public async Task GetActive_ExcludesInactive()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Aktiv", active: true), Leader());
        await svc.CreateAsync(Input("Inaktiv", active: false), Leader());

        var active = await svc.GetActiveAsync();
        Assert.Single(active);
        Assert.Equal("Aktiv", active[0].Name);
        Assert.Equal(2, (await svc.GetAllAsync()).Count);
    }

    [Fact]
    public async Task NonLeadership_CannotCreate()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input("X"), Junior()));
    }
}
