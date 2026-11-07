using Microsoft.Extensions.Caching.Memory;
using NOOSE_Website.Infrastructure;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The phone column choice is kept per agent and list in the navigation preferences.</summary>
public sealed class MobileColumnsPreferenceTests
{
    private static NavPreferencesService Service(SqliteTestContext ctx)
        => new(ctx.Factory, new MemoryCache(new MemoryCacheOptions()));

    private static void SeedAgent(SqliteTestContext ctx, string id)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent(id));
        db.SaveChanges();
    }

    [Fact]
    public async Task SetMobileColumns_persists_per_list()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1");

        await Service(ctx).SetMobileColumnsAsync("a1", "personen", ["name", "einstufung"]);
        await Service(ctx).SetMobileColumnsAsync("a1", "fraktionen", ["name"]);
        await Service(ctx).SetMobileColumnsAsync("a1", "personen", ["name", "gefaehrdung", "name"]);

        // a fresh service and cache: what comes back was read from the row
        var stored = (await Service(ctx).GetAsync("a1")).MobileColumns;
        Assert.Equal(["name", "gefaehrdung"], stored["personen"]);
        Assert.Equal(["name"], stored["fraktionen"]);
    }

    [Fact]
    public async Task SetMobileColumns_rejects_empty()
    {
        using var ctx = new SqliteTestContext();
        SeedAgent(ctx, "a1");
        var prefs = Service(ctx);

        await Assert.ThrowsAsync<ArgumentException>(() => prefs.SetMobileColumnsAsync("a1", "personen", []));
        await Assert.ThrowsAsync<ArgumentException>(() => prefs.SetMobileColumnsAsync("a1", "personen", [" "]));
        await Assert.ThrowsAsync<ArgumentException>(() => prefs.SetMobileColumnsAsync("a1", " ", ["name"]));

        Assert.Empty((await Service(ctx).GetAsync("a1")).MobileColumns);
    }

    [Fact]
    public async Task SetMobileColumns_in_the_demo_stays_with_the_connection()
    {
        using var ctx = new SqliteTestContext();
        var visitor = Service(ctx);

        await visitor.SetMobileColumnsAsync(DemoIdentity.AgentId, "personen", ["aliase"]);

        Assert.Equal(["aliase"], (await visitor.GetAsync(DemoIdentity.AgentId)).MobileColumns["personen"]);
        // the next visitor shares the account, not the choice
        Assert.Empty((await Service(ctx).GetAsync(DemoIdentity.AgentId)).MobileColumns);
    }
}
