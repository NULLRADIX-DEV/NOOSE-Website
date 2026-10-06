using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PartnerAgencyProfileService"/> against in-memory SQLite.</summary>
public sealed class PartnerAgencyProfileServiceTests : IDisposable
{
    private readonly SqliteTestContext _ctx = new();
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public void Dispose()
    {
        _cache.Dispose();
        _ctx.Dispose();
    }

    private PartnerAgencyProfileService NewService() => new(_ctx.Factory, _cache);

    private static ClaimsPrincipal Admin() => ClaimsPrincipalBuilder.Agent("admin").AsAdmin().Build();

    private static PartnerRuleView Rule(string type, PartnerRuleScope scope, bool whole = false) => new(type, scope, whole);

    [Fact]
    public async Task Get_WithoutRow_ReturnsDefaults()
    {
        var view = await NewService().GetAsync(PartnerAgency.LSPD);

        Assert.Equal(PartnerFeature.None, view.Features);
        Assert.Equal(PartnerContent.None, view.BlockedContent);
        Assert.Empty(view.Rules);
    }

    [Fact]
    public async Task Save_RequiresAdmin()
    {
        var leader = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => NewService().SaveAsync(
            PartnerAgency.LSPD, PartnerFeature.None, PartnerContent.Docs, [], leader));
    }

    [Fact]
    public async Task Save_PersistsProfileAndRules_AndEvictsTheCache()
    {
        var svc = NewService();
        await svc.GetAsync(PartnerAgency.Parlament); // warm the cache

        await svc.SaveAsync(PartnerAgency.Parlament, PartnerFeature.Graph, PartnerContent.Docs | PartnerContent.Comments,
            [Rule("Faction", PartnerRuleScope.BadFactions, whole: true), Rule("Law", PartnerRuleScope.All)], Admin());

        var view = await svc.GetAsync(PartnerAgency.Parlament);
        Assert.Equal(PartnerFeature.Graph, view.Features);
        Assert.Equal(PartnerContent.Docs | PartnerContent.Comments, view.BlockedContent);
        Assert.Equal(PartnerRuleScope.BadFactions, view.RuleFor("Faction")!.Scope);
        Assert.True(view.RuleFor("Faction")!.IncludesChildren);
        Assert.Equal(PartnerRuleScope.All, view.RuleFor("Law")!.Scope);
    }

    [Fact]
    public async Task Save_UpdatesAndRemovesExistingRules()
    {
        var svc = NewService();
        await svc.SaveAsync(PartnerAgency.Parlament, PartnerFeature.None, PartnerContent.None,
            [Rule("Faction", PartnerRuleScope.All), Rule("Law", PartnerRuleScope.All)], Admin());

        await svc.SaveAsync(PartnerAgency.Parlament, PartnerFeature.None, PartnerContent.None,
            [Rule("Faction", PartnerRuleScope.BadFactions)], Admin());

        await using var db = _ctx.NewContext();
        var rows = await db.PartnerReleaseRules.Where(r => r.Agency == PartnerAgency.Parlament).ToListAsync();
        var row = Assert.Single(rows);
        Assert.Equal("Faction", row.EntityType);
        Assert.Equal(PartnerRuleScope.BadFactions, row.Scope);
    }

    [Theory]
    [InlineData("Person", PartnerRuleScope.BadFactions)]
    [InlineData("Faction", PartnerRuleScope.BadFactionMembers)]
    [InlineData("PersonDoc", PartnerRuleScope.All)]
    [InlineData("Unknown", PartnerRuleScope.All)]
    public async Task Save_RejectsARuleThatDoesNotFitItsType(string type, PartnerRuleScope scope)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => NewService().SaveAsync(
            PartnerAgency.Parlament, PartnerFeature.None, PartnerContent.None, [Rule(type, scope)], Admin()));
    }

    [Fact]
    public async Task HasFeature_IsTrueOnlyForPartnersOfThatAgency()
    {
        var svc = NewService();
        await svc.SaveAsync(PartnerAgency.Parlament, PartnerFeature.Graph, PartnerContent.None, [], Admin());

        var parlament = ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.Parlament, PartnerRank.Member).Build();
        var lspd = ClaimsPrincipalBuilder.Agent("l").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();
        var internalAgent = ClaimsPrincipalBuilder.Agent("i").WithRank(Rank.Director).Build();

        Assert.True(await svc.HasFeatureAsync(parlament, PartnerFeature.Graph));
        Assert.False(await svc.HasFeatureAsync(parlament, PartnerFeature.Radio));
        Assert.False(await svc.HasFeatureAsync(lspd, PartnerFeature.Graph));
        Assert.False(await svc.HasFeatureAsync(internalAgent, PartnerFeature.Graph));
    }
}
