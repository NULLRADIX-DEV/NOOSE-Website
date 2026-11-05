using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The sync stamp and error live on the agent row but are written by ExecuteUpdate only.</summary>
public class DiscordRoleStampTests
{
    [Fact]
    public async Task A_stale_user_manager_copy_keeps_the_sync_stamp()
    {
        using var ctx = new SqliteTestContext();
        using (var seed = ctx.NewContext())
        {
            seed.Users.Add(Seed.Agent("a"));
            await seed.SaveChangesAsync();
        }

        // a long-lived context holds the account from before the sync, as UserManager's does
        using var stale = ctx.NewContext();
        var copy = stale.Users.Single(u => u.Id == "a");

        var stamp = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            await db.Users.Where(u => u.Id == "a").ExecuteUpdateAsync(s => s
                .SetProperty(u => u.DiscordRolesSyncAt, stamp)
                .SetProperty(u => u.DiscordRolesSyncError, "Rolle nicht gefunden"));
        }

        copy.Codename = "Neu";
        stale.Update(copy);
        await stale.SaveChangesAsync();

        using var fresh = ctx.NewContext();
        var agent = fresh.Users.Single(u => u.Id == "a");
        Assert.Equal(stamp, agent.DiscordRolesSyncAt);
        Assert.Equal("Rolle nicht gefunden", agent.DiscordRolesSyncError);
        Assert.Equal("Neu", agent.Codename);
    }
}
