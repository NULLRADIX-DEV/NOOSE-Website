using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Financing;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The funding-request visibility rule: own records only, leadership and read-only supervision see all.</summary>
public sealed class FinancingVisibilityTests
{
    private static async Task<(string Mine, string Foreign)> SeedAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        var mine = new FinancingRequest
        {
            CaseNumber = "FIB-FIN-2026-0001", AgentId = "me", Justification = "Meiner",
        };
        var foreign = new FinancingRequest
        {
            CaseNumber = "FIB-FIN-2026-0002", AgentId = "someone-else", Justification = "Fremder",
        };
        db.FinancingRequests.AddRange(mine, foreign);
        await db.SaveChangesAsync();
        return (mine.Id, foreign.Id);
    }

    [Fact]
    public async Task OnlyVisible_AgentSeesOnlyTheirOwn()
    {
        using var ctx = new SqliteTestContext();
        var (mine, _) = await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        var rows = await db.FinancingRequests.OnlyVisible(mayAll: false, meId: "me").ToListAsync();

        Assert.Single(rows);
        Assert.Equal(mine, rows[0].Id);
    }

    [Fact]
    public async Task OnlyVisible_ReaderOfEverythingSeesAll()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        Assert.Equal(2, await db.FinancingRequests.OnlyVisible(mayAll: true, meId: "lead").CountAsync());
    }

    [Fact]
    public async Task OnlyVisible_FailsClosedWithoutAgentContext()
    {
        using var ctx = new SqliteTestContext();
        await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        Assert.Empty(await db.FinancingRequests.OnlyVisible(mayAll: false, meId: null).ToListAsync());
        Assert.Empty(await db.FinancingRequests.OnlyVisible(mayAll: false, meId: string.Empty).ToListAsync());
    }

    [Fact]
    public async Task IsVisible_MatchesTheQueryFilter()
    {
        using var ctx = new SqliteTestContext();
        var (mine, foreign) = await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        Assert.True(await FinancingVisibility.IsVisibleAsync(db, mine, mayAll: false, meId: "me"));
        Assert.False(await FinancingVisibility.IsVisibleAsync(db, foreign, mayAll: false, meId: "me"));
        Assert.True(await FinancingVisibility.IsVisibleAsync(db, foreign, mayAll: true, meId: "lead"));
        Assert.False(await FinancingVisibility.IsVisibleAsync(db, foreign, mayAll: false, meId: null));
        // a record that does not exist is never visible, not even to leadership
        Assert.False(await FinancingVisibility.IsVisibleAsync(db, "missing", mayAll: true, meId: "lead"));
    }

    [Fact]
    public async Task VisibleIds_FiltersABatch()
    {
        using var ctx = new SqliteTestContext();
        var (mine, foreign) = await SeedAsync(ctx);
        var both = new[] { mine, foreign };

        await using var db = ctx.NewContext();
        Assert.Equal(new[] { mine }, (await FinancingVisibility.VisibleIdsAsync(db, both, false, "me")).ToArray());
        Assert.Equal(2, (await FinancingVisibility.VisibleIdsAsync(db, both, true, "lead")).Count);
        Assert.Empty(await FinancingVisibility.VisibleIdsAsync(db, both, false, null));
        Assert.Empty(await FinancingVisibility.VisibleIdsAsync(db, [], false, "me"));
    }

    [Fact]
    public async Task CentralVisibility_GatesForeignRequests()
    {
        using var ctx = new SqliteTestContext();
        var (mine, foreign) = await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        var agent = new ViewerScope(MayClassifiedRead: false, MayAllTaskforces: false, MeId: "me", PartnerAgency: null);
        var leader = new ViewerScope(MayClassifiedRead: true, MayAllTaskforces: true, MeId: "lead", PartnerAgency: null);

        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(FinancingRequest), mine, agent));
        // must NOT fall through to the "unknown type is visible" tail
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(FinancingRequest), foreign, agent));
        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(FinancingRequest), foreign, leader));
    }

    [Fact]
    public async Task CentralVisibility_HidesFundingFromPartners()
    {
        using var ctx = new SqliteTestContext();
        var (mine, _) = await SeedAsync(ctx);

        await using var db = ctx.NewContext();
        var partner = new ViewerScope(MayClassifiedRead: false, MayAllTaskforces: false, MeId: "p",
            PartnerAgency: PartnerAgency.LSPD);

        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(FinancingRequest), mine, partner));
    }
}
