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
        => new(_ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static ClaimsPrincipal Admin()
        => ClaimsPrincipalBuilder.Agent("admin").AsAdmin().Build();

    private static ClaimsPrincipal Partner(string id, PartnerAgency agency, PartnerRank rank)
        => ClaimsPrincipalBuilder.Agent(id).AsPartner(agency, rank).Build();

    private void SeedConfig(PartnerVisibilityConfig cfg)
    {
        using var db = _ctx.NewContext();
        db.SystemSettings.Add(new SystemSetting { Key = SettingKey, Value = JsonSerializer.Serialize(cfg) });
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

    private PartnerVisibilityConfig? ReadConfigFromDb()
    {
        using var db = _ctx.NewContext();
        var raw = db.SystemSettings.AsNoTracking().FirstOrDefault(e => e.Key == SettingKey)?.Value;
        return string.IsNullOrWhiteSpace(raw) ? null : JsonSerializer.Deserialize<PartnerVisibilityConfig>(raw);
    }

    public void Dispose() => _ctx.Dispose();

    // ---- GetAsync ----

    [Fact]
    public async Task GetAsync_NoSetting_ReturnsEmptyConfig()
    {
        var svc = NewService();

        var cfg = await svc.GetAsync();

        Assert.NotNull(cfg);
        Assert.Empty(cfg.Ranks);
    }

    [Fact]
    public async Task GetAsync_WithSetting_ReturnsParsedConfig()
    {
        var stored = new PartnerVisibilityConfig();
        stored.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Chief)] =
            new PartnerRankVisibility { Types = { "Person", "Case" } };
        SeedConfig(stored);

        var cfg = await NewService().GetAsync();

        var entry = cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Chief)];
        Assert.Contains("Person", entry.Types);
        Assert.Contains("Case", entry.Types);
    }

    [Fact]
    public async Task GetAsync_CachesFirstResult()
    {
        var svc = NewService();

        // First call with no row caches the empty config.
        var first = await svc.GetAsync();
        Assert.Empty(first.Ranks);

        // Row added afterwards is not observed because the cache still holds the empty config.
        var later = new PartnerVisibilityConfig();
        later.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.DoJ, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Person" } };
        SeedConfig(later);

        var second = await svc.GetAsync();
        Assert.Empty(second.Ranks);
    }

    // ---- GetRankAsync ----

    [Fact]
    public async Task GetRankAsync_ConfiguredRank_ReturnsEntry()
    {
        var stored = new PartnerVisibilityConfig();
        stored.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSMD, PartnerRank.Special)] =
            new PartnerRankVisibility { Types = { "Faction" } };
        SeedConfig(stored);

        var entry = await NewService().GetRankAsync(PartnerAgency.LSMD, PartnerRank.Special);

        Assert.NotNull(entry);
        Assert.Contains("Faction", entry!.Types);
    }

    [Fact]
    public async Task GetRankAsync_UnconfiguredRank_ReturnsNull()
    {
        var entry = await NewService().GetRankAsync(PartnerAgency.LSPD, PartnerRank.Member);

        Assert.Null(entry);
    }

    // ---- SaveRankAsync ----

    [Fact]
    public async Task SaveRankAsync_Admin_CreatesRowWhenAbsent()
    {
        var vis = new PartnerRankVisibility { Types = { "Faction" } };

        await NewService().SaveRankAsync(PartnerAgency.LSPD, PartnerRank.Special, vis, Admin());

        var cfg = ReadConfigFromDb();
        Assert.NotNull(cfg);
        var key = PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Special);
        Assert.True(cfg!.Ranks.ContainsKey(key));
        Assert.Contains("Faction", cfg.Ranks[key].Types);
    }

    [Fact]
    public async Task SaveRankAsync_Admin_UpdatesExistingRow()
    {
        var seeded = new PartnerVisibilityConfig();
        seeded.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.DoJ, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Person" } };
        SeedConfig(seeded);

        await NewService().SaveRankAsync(
            PartnerAgency.LSPD, PartnerRank.Chief,
            new PartnerRankVisibility { Types = { "Case" } }, Admin());

        var cfg = ReadConfigFromDb();
        Assert.NotNull(cfg);
        Assert.True(cfg!.Ranks.ContainsKey(PartnerVisibilityConfig.RankKey(PartnerAgency.DoJ, PartnerRank.Member)));
        Assert.True(cfg.Ranks.ContainsKey(PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Chief)));

        // Only one settings row exists (updated, not duplicated).
        using var db = _ctx.NewContext();
        Assert.Equal(1, db.SystemSettings.Count(e => e.Key == SettingKey));
    }

    [Fact]
    public async Task SaveRankAsync_NullVisibility_RemovesEntry()
    {
        var seeded = new PartnerVisibilityConfig();
        var key = PartnerVisibilityConfig.RankKey(PartnerAgency.DoJ, PartnerRank.Member);
        seeded.Ranks[key] = new PartnerRankVisibility { Types = { "Person" } };
        SeedConfig(seeded);

        await NewService().SaveRankAsync(PartnerAgency.DoJ, PartnerRank.Member, null, Admin());

        var cfg = ReadConfigFromDb();
        Assert.NotNull(cfg);
        Assert.False(cfg!.Ranks.ContainsKey(key));
    }

    [Fact]
    public async Task SaveRankAsync_NonAdmin_Throws()
    {
        var actor = ClaimsPrincipalBuilder.Agent("u").WithRank(Rank.Director).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => NewService().SaveRankAsync(
                PartnerAgency.LSPD, PartnerRank.Member,
                new PartnerRankVisibility { Types = { "Person" } }, actor));

        // Nothing was persisted.
        using var db = _ctx.NewContext();
        Assert.Equal(0, db.SystemSettings.Count(e => e.Key == SettingKey));
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
    public async Task GetAllowedTypesAsync_PartnerUnconfiguredRank_ReturnsReleasedTypesOnly()
    {
        SeedShare("Faction", "f1", null, PartnerAgency.LSPD);
        SeedRule(PartnerAgency.LSPD, "Law", PartnerRuleScope.All);
        SeedShare("Case", "c1", null, PartnerAgency.DoJ);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.NotNull(allowed);
        Assert.Equal(new[] { "Document", "Faction", "Law" }, allowed!.OrderBy(t => t));
    }

    [Fact]
    public async Task GetAllowedTypesAsync_ConfiguredRank_ReturnsAllowlistNarrowedToReleased()
    {
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Person", "Case" } };
        SeedConfig(cfg);
        SeedShare("Person", "p1", null, PartnerAgency.LSPD);

        var allowed = await NewService().GetAllowedTypesAsync(Partner("me", PartnerAgency.LSPD, PartnerRank.Member));

        Assert.NotNull(allowed);
        Assert.Equal(new[] { "Person" }, allowed!);
    }

    [Fact]
    public async Task GetAllowedTypesAsync_IndividualShare_WidensAllowlist()
    {
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Person" } };
        SeedConfig(cfg);
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
    public async Task GetVisibleTabsAsync_NoRankEntry_ReturnsNull()
    {
        var user = Partner("me", PartnerAgency.LSPD, PartnerRank.Member);

        var tabs = await NewService().GetVisibleTabsAsync(user, "Person", "r1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_TabRestriction_ReturnsSlugs()
    {
        var cfg = new PartnerVisibilityConfig();
        var key = PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member);
        cfg.Ranks[key] = new PartnerRankVisibility
        {
            Types = { "Person" },
            Tabs = { ["Person"] = new List<string> { "overview", "docs" } },
        };
        SeedConfig(cfg);

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
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Faction" } };
        SeedConfig(cfg);

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Faction", "f1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_UnlistedType_ReturnsEmpty()
    {
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Person" } };
        SeedConfig(cfg);

        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Vehicle", "v1");

        Assert.NotNull(tabs);
        Assert.Empty(tabs!);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_IndividualShare_ReturnsNull()
    {
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.LSPD, PartnerRank.Member)] =
            new PartnerRankVisibility
            {
                Types = { "Person" },
                Tabs = { ["Person"] = new List<string> { "overview" } },
            };
        SeedConfig(cfg);
        SeedShare("Person", "r1", "me", PartnerAgency.LSPD);

        // Individually released record shows in full (null) despite the rank tab restriction.
        var tabs = await NewService().GetVisibleTabsAsync(
            Partner("me", PartnerAgency.LSPD, PartnerRank.Member), "Person", "r1");

        Assert.Null(tabs);
    }

    [Fact]
    public async Task GetVisibleTabsAsync_BlockedContent_RemovesTabsOfAnUnconfiguredRank()
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
        var cfg = new PartnerVisibilityConfig();
        cfg.Ranks[PartnerVisibilityConfig.RankKey(PartnerAgency.Parlament, PartnerRank.Member)] =
            new PartnerRankVisibility { Types = { "Faction" } };
        SeedConfig(cfg);
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
