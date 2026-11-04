using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Financing;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="FinancingCatalogService"/> over in-memory SQLite.</summary>
public sealed class FinancingCatalogServiceTests
{
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().Build();

    private static FinancingCatalogService Build(SqliteTestContext ctx)
        => new(ctx.Factory, Substitute.For<IProfileSuggestionService>());

    private static FinancingItemInput Input(string name, decimal price = 1_000m, int percent = 100,
        int max = 1, Rank min = Rank.Trainee, bool active = true, string? category = null, int sorting = 0)
        => new()
        {
            Name = name,
            UnitPrice = price,
            SubsidyPercent = percent,
            MaxQuantity = max,
            MinimumRank = min,
            IsActive = active,
            Category = category,
            Sorting = sorting,
        };

    [Fact]
    public async Task Create_PersistsAndTrimsTheName()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        var created = await svc.CreateAsync(Input("  Schutzweste  ", category: "  Ausrüstung  "), Leader());

        Assert.Equal("Schutzweste", created.Name);
        Assert.Equal("Ausrüstung", created.Category);
        Assert.Single(await svc.GetAllAsync());
    }

    [Fact]
    public async Task Create_RejectsDuplicateNameAmongLiveItems()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Schutzweste"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("Schutzweste"), Leader()));
    }

    [Fact]
    public async Task Create_RejectsInvalidFigures()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("   "), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("A", price: 0m), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("B", percent: 0), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("C", percent: 101), Leader()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("D", max: 0), Leader()));
    }

    [Fact]
    public async Task Create_RejectsAFractionalUnitPrice()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        // a fractional price could round the subsidy above the goods value and make the own share negative
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input("Halber", price: 10.50m), Leader()));
        await svc.CreateAsync(Input("Ganzer", price: 11m), Leader());
    }

    [Fact]
    public async Task Write_DeniedForNonLeadershipAndReadOnlySupervision()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input("A"), Junior()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input("A"), OnlyReader()));
    }

    [Fact]
    public async Task GetActive_ExcludesInactive_AndOrdersBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Zebra", sorting: 1), Leader());
        await svc.CreateAsync(Input("Alpha", sorting: 1), Leader());
        await svc.CreateAsync(Input("Erste", sorting: 0), Leader());
        await svc.CreateAsync(Input("Inaktiv", active: false), Leader());

        var active = await svc.GetActiveAsync();

        Assert.Equal(new[] { "Erste", "Alpha", "Zebra" }, active.Select(i => i.Name).ToArray());
        Assert.Equal(4, (await svc.GetAllAsync()).Count);
    }

    [Fact]
    public async Task GetActiveFor_FiltersByMinimumRank()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Für alle", min: Rank.Trainee), Leader());
        await svc.CreateAsync(Input("Nur Führung", min: Rank.SpecialAgentInCharge), Leader());

        var forSpecial = await svc.GetActiveForAsync(Rank.SpecialAgent, Division.Cid);
        Assert.Equal(new[] { "Für alle" }, forSpecial.Select(i => i.Name).ToArray());

        var forDirector = await svc.GetActiveForAsync(Rank.Director, Division.Cid);
        Assert.Equal(2, forDirector.Count);
    }

    [Fact]
    public async Task GetActiveFor_UsesTheThresholdOfTheHomeDivision()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var input = Input("Kampfschrotflinte", min: Rank.AssistantDirector);
        input.MinimumRankHrt = Rank.SpecialAgent;
        await svc.CreateAsync(input, Leader());

        Assert.Single(await svc.GetActiveForAsync(Rank.SpecialAgent, Division.Hrt));
        Assert.Empty(await svc.GetActiveForAsync(Rank.SpecialAgent, Division.Cid));
        // empty CTD follows CID
        Assert.Empty(await svc.GetActiveForAsync(Rank.SectionChief, Division.Ctd));
        Assert.Single(await svc.GetActiveForAsync(Rank.AssistantDirector, Division.Ctd));
        // no division: base value
        Assert.Empty(await svc.GetActiveForAsync(Rank.SpecialAgent, null));
    }

    [Fact]
    public async Task Create_StoresADivisionEqualToTheBaseAsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var input = Input("Messer", min: Rank.SpecialAgent);
        input.MinimumRankCtd = Rank.SpecialAgent;
        input.MinimumRankHrt = Rank.SeniorSpecialAgent;

        var created = await svc.CreateAsync(input, Leader());

        Assert.Null(created.MinimumRankCtd);
        Assert.Equal(Rank.SeniorSpecialAgent, created.MinimumRankHrt);
    }

    [Fact]
    public async Task GetActiveFor_UnrankedAgentSeesNothing()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Für alle"), Leader());

        Assert.Empty(await svc.GetActiveForAsync(null, Division.Cid));
    }

    [Fact]
    public async Task Update_ChangesTheItem_AndKeepsItsOwnNameFree()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var created = await svc.CreateAsync(Input("Schutzweste", price: 1_000m), Leader());

        await svc.UpdateAsync(created.Id, Input("Schutzweste", price: 2_500m, percent: 60), Leader());

        var reloaded = await svc.GetAsync(created.Id);
        Assert.Equal(2_500m, reloaded!.UnitPrice);
        Assert.Equal(60, reloaded.SubsidyPercent);
    }

    [Fact]
    public async Task Update_RejectsANameTakenByAnotherItem()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        await svc.CreateAsync(Input("Erste"), Leader());
        var second = await svc.CreateAsync(Input("Zweite"), Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync(second.Id, Input("Erste"), Leader()));
    }

    [Fact]
    public async Task Delete_SoftDeletes_AndFreesTheNameForReuse()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx);
        var created = await svc.CreateAsync(Input("Schutzweste"), Leader());

        // the soft-delete interceptor is not wired in the test context, so mark the row directly
        await using (var db = ctx.NewContext())
        {
            var row = await db.FinancingItems.FirstAsync(i => i.Id == created.Id);
            row.IsDeleted = true;
            row.DeletedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Empty(await svc.GetAllAsync());
        // a deleted ghost must not block the name
        var again = await svc.CreateAsync(Input("Schutzweste"), Leader());
        Assert.NotEqual(created.Id, again.Id);
    }

    [Fact]
    public async Task Create_StagesTheCategoryOnTheSameContext()
    {
        using var ctx = new SqliteTestContext();
        var suggestions = Substitute.For<IProfileSuggestionService>();
        var svc = new FinancingCatalogService(ctx.Factory, suggestions);

        await svc.CreateAsync(Input("Schutzweste", category: "Ausrüstung"), Leader());

        await suggestions.Received(1).StageAsync(
            Arg.Any<NOOSE_Website.Data.AppDbContext>(),
            SuggestionType.FinancingCategory,
            Arg.Is<IEnumerable<string>>(v => v.Contains("Ausrüstung")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_WithoutCategory_StagesNothing()
    {
        using var ctx = new SqliteTestContext();
        var suggestions = Substitute.For<IProfileSuggestionService>();
        var svc = new FinancingCatalogService(ctx.Factory, suggestions);

        await svc.CreateAsync(Input("Ohne Kategorie"), Leader());

        await suggestions.DidNotReceive().StageAsync(
            Arg.Any<NOOSE_Website.Data.AppDbContext>(),
            Arg.Any<SuggestionType>(),
            Arg.Any<IEnumerable<string>>(),
            Arg.Any<CancellationToken>());
    }
}
