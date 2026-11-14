using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for PartnerVisibilityPolicyService against in-memory SQLite.</summary>
public sealed class PartnerVisibilityPolicyServiceTests : IDisposable
{
    private const string SettingKey = "PartnerRangSichtbarkeit";

    private readonly SqliteTestContext _ctx = new();

    // Fresh cache per service instance keeps the 10s cache isolated per test.
    private PartnerVisibilityPolicyService NewService()
        => new(_ctx.Factory, Units());

    private PartnerUnitService Units() => new(_ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal Admin()
        => ClaimsPrincipalBuilder.Agent("admin").AsAdmin().Build();

    private static ClaimsPrincipal Partner(string id, PartnerAgency agency, PartnerRank rank)
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(agency, rank).Build();

    private void SeedUnit(PartnerAgency agency, PartnerRank rank, PartnerUnitVisibility visibility)
    {
        using var db = _ctx.NewContext();
        db.PartnerUnits.Find(PartnerUnits.ForLegacy(agency, rank))!.VisibilityJson = JsonSerializer.Serialize(visibility);
        db.SaveChanges();
    }

    private void SeedShare(string entityType, string entityId, string? partnerAgentId, PartnerAgency agency)
    {
        using var db = _ctx.NewContext();
        db.PartnerShares.Add(new PartnerShare
        {
            EntityType = entityType,
            EntityId = entityId,
            PartnerAgentId = partnerAgentId,
            Agency = agency,
        });
        db.SaveChanges();
    }

    private void SeedRule(PartnerAgency agency, string entityType, PartnerRuleScope scope)
    {
        using var db = _ctx.NewContext();
        db.PartnerReleaseRules.Add(new PartnerReleaseRule { Agency = agency, EntityType = entityType, Scope = scope });
        db.SaveChanges();
    }

    private void SeedBlocked(PartnerAgency agency, PartnerContent blocked)
    {
        using var db = _ctx.NewContext();
        db.PartnerAgencyProfiles.Add(new PartnerAgencyProfile { Agency = agency, BlockedContent = blocked });
        db.SaveChanges();
    }

    public void Dispose() => _ctx.Dispose();

    // ---- unit allowlist (PartnerUnitService) ----

    [Fact]
    public async Task SaveVisibilityAsync_Admin_StoresOnTheUnit()
    {
        await Units().SaveVisibilityAsync(PartnerUnits.DojSid, new PartnerUnitVisibility { Types = { "Faction" } }, Admin());

        var unit = await Units().GetAsync(PartnerUnits.DojSid);
        Assert.Equal(new[] { "Faction" }, unit!.Visibility!.Types);
    }

    [Fact]
    public async Task SaveVisibilityAsync_Null_ClearsTheRestriction()
    {
        SeedUnit(PartnerAgency.DoJ, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Person" } });

        await Units().SaveVisibilityAsync(PartnerUnits.DojSid, null, Admin());

        Assert.Null((await Units().GetAsync(PartnerUnits.DojSid))!.Visibility);
    }

    [Fact]
    public async Task SaveVisibilityAsync_NonAdmin_Throws()
    {
        var actor = ClaimsPrincipalBuilder.Agent("u").WithRank(Rank.Director).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Units().SaveVisibilityAsync(
            PartnerUnits.DojSid, new PartnerUnitVisibility { Types = { "Person" } }, actor));
    }

    [Fact]
    public async Task SyncAsync_MovesTheOldTierAllowlistOntoTheSeededUnitOnce()
    {
        var legacy = new PartnerVisibilityConfig();
        legacy.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Chief)] = new PartnerUnitVisibility
        {
            Types = { "Taskforce" },
            Tabs = { ["Taskforce"] = new List<string> { "stammdaten" } },
        };
        using (var db = _ctx.NewContext())
        {
            db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = JsonSerializer.Serialize(legacy) });
            db.SaveChanges();
            await PartnerUnitService.SyncAsync(db);
        }

        var unit = await Units().GetAsync(PartnerUnits.ForLegacy(PartnerAgency.LSPD, PartnerRank.Chief));
        Assert.Equal(new[] { "Taskforce" }, unit!.Visibility!.Types);
        // the new investigations tab joins an explicit old tab list
        Assert.Contains("ermittlungen", unit.Visibility.Tabs["Taskforce"]);

        // a second run leaves an admin change alone
        await Units().SaveVisibilityAsync(unit.Id, null, Admin());
        using (var db = _ctx.NewContext())
        {
            await PartnerUnitService.SyncAsync(db);
        }
        Assert.Null((await Units().GetAsync(unit.Id))!.Visibility);
    }

    [Fact]
    public async Task SyncAsync_GivesAPartnerWithoutUnitTheSeededUnitOfItsTier()
    {
        using (var db = _ctx.NewContext())
        {
            db.Users.Add(new NOOSE_Website.Data.Entities.Agent
            {
                Id = "old", UserName = "old", Codename = "Old", Status = AgentStatus.Active,
                PartnerAgency = PartnerAgency.DoJ, PartnerRank = PartnerRank.Special,
            });
            db.SaveChanges();
            await PartnerUnitService.SyncAsync(db);
        }

        using var check = _ctx.NewContext();
        Assert.Equal(PartnerUnits.DojStateAttorney, check.Users.AsNoTracking().Single(u => u.Id == "old").PartnerUnitId);
    }

    // ---- GetAllowedTypesAsync ----

    [Fact]
    public async Task GetAllowedTypesAsync_InternalUser_ReturnsNull()
    {
        var user = ClaimsPrincipalBuilder.Agent("internal").Build();

        var allowed = await NewService().GetAllowedTypesAsync(user);

        Assert.Null(allowed);
    }

    [Fact]
    public async Task GetAllowedTypesAsync_PartnerUnconfiguredUnit_ReturnsReleasedTypesOnly()
    {
        SeedShare("Faction", "f1", null, PartnerAgency.LSPD);
        SeedRule(PartnerAgency.LSPD, "Law", PartnerRuleScope.All);
        SeedShare("Case", "c1", null, PartnerAgency.DoJ);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.NotNull(allowed);
        Assert.Equal(new[] { "Document", "Faction", "Law" }, allowed!.OrderBy(t => t));
    }

    [Fact]
    public async Task GetAllowedTypesAsync_ConfiguredUnit_ReturnsAllowlistNarrowedToReleased()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Person", "Case" } });
        SeedShare("Person", "p1", null, PartnerAgency.LSPD);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.NotNull(allowed);
        Assert.Equal(new[] { "Person" }, allowed!);
    }

    [Fact]
    public async Task GetAllowedTypesAsync_IndividualShare_WidensAllowlist()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Person" } });
        SeedShare("Person", "p1", null, PartnerAgency.LSPD);
        SeedShare("Faction", "f1", "me", PartnerAgency.LSPD);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.NotNull(allowed);
        Assert.Contains("Person", allowed);
        Assert.Contains("Faction", allowed);
    }

    [Fact]
    public async Task GetAllowedTypesAsync_AnotherAccountsIndividualShare_DoesNotCount()
    {
        SeedShare("Faction", "f1", "someone-else", PartnerAgency.LSPD);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.DoesNotContain("Faction", allowed!);
    }

    // ---- GetVisibleTabsAsync ----

    [Fact]
    public async Task GetVisibleTabsAsync_InternalUser_ReturnsNull()
    {
        var user = ClaimsPrincipalBuilder.Agent("internal").Build();

        var tabs = await NewService().GetVisibleTabsAsync(user, "Person", "r1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_NoUnitAllowlist_ReturnsNull()
    {
        var user = Partner("me", PartnerAgency.LSPD, PartnerRank.Member);

        var tabs = await NewService().GetVisibleTabsAsync(user, "Person", "r1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_TabRestriction_ReturnsSlugs()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility
        {
            Types = { "Person" },
            Tabs = { ["Person"] = new List<string> { "overview", "docs" } },
        });

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Person", "r1");

        Assert.NotNull(tabs);
        Assert.Equal(2, tabs!.Count);
        Assert.Contains("overview", tabs);
        Assert.Contains("docs", tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_ListedTypeWithoutRestriction_ReturnsNull()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Faction" } });

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Faction", "f1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_UnlistedType_ReturnsEmpty()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Person" } });

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Vehicle", "v1");

        Assert.NotNull(tabs);
        Assert.Empty(tabs!);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_IndividualShare_ReturnsNull()
    {
        SeedUnit(PartnerAgency.LSPD, PartnerRank.Member, new PartnerUnitVisibility
            {
                Types = { "Person" },
                Tabs = { ["Person"] = new List<string> { "overview" } },
            });
        SeedShare("Person", "r1", "me", PartnerAgency.LSPD);

        // Individually released record shows in full (null) despite the unit tab restriction.
        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Person", "r1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_BlockedContent_RemovesTabsOfAnUnconfiguredUnit()
    {
        SeedBlocked(PartnerAgency.Parlament, PartnerContent.Docs | PartnerContent.Observations);

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.Parlament, PartnerRank.Member), "Person", "r1");

        Assert.NotNull(tabs);
        Assert.DoesNotContain("doks", tabs!);
        Assert.DoesNotContain("ueberwachung", tabs!);
        Assert.Contains("steckbrief", tabs!);
        Assert.Contains("einstufung", tabs!);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_BlockedContent_BeatsAnIndividualRelease()
    {
        SeedUnit(PartnerAgency.Parlament, PartnerRank.Member, new PartnerUnitVisibility { Types = { "Faction" } });
        SeedShare("Faction", "f1", "me", PartnerAgency.Parlament);
        SeedBlocked(PartnerAgency.Parlament, PartnerContent.Docs);

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.Parlament, PartnerRank.Member), "Faction", "f1");

        Assert.NotNull(tabs);
        Assert.DoesNotContain("doks", tabs!);
        Assert.Contains("mitglieder", tabs!);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_OtherAgencysBlock_DoesNotApply()
    {
        SeedBlocked(PartnerAgency.Parlament, PartnerContent.Docs);

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.DoJ, PartnerRank.Member), "Person", "r1");

        Assert.Null(tabs);
    }
}
