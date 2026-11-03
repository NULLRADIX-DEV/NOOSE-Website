using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The one place that decides what "active stock" means.</summary>
public sealed class RecordArchiveTests
{
    private static async Task<SqliteTestContext> TwoPeopleAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("agent-1"));
        db.People.Add(Seed.Person("aktiv", "Aktiv"));
        db.People.Add(Seed.Person("archiv", "Archiviert", p => p.IsArchived = true));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static System.Security.Claims.ClaimsPrincipal Actor
        => ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.Trainee).Build();

    [Fact]
    public async Task OnlyActive_drops_archived_rows()
    {
        using var ctx = await TwoPeopleAsync();
        await using var db = ctx.NewContext();
        var ids = await db.People.OnlyActive().Select(p => p.Id).ToListAsync();
        Assert.Equal(["aktiv"], ids);
    }

    [Fact]
    public async Task OnlyArchived_keeps_only_archived_rows()
    {
        using var ctx = await TwoPeopleAsync();
        await using var db = ctx.NewContext();
        var ids = await db.People.OnlyArchived().Select(p => p.Id).ToListAsync();
        Assert.Equal(["archiv"], ids);
    }

    [Theory]
    [InlineData(ArchiveFilter.Active, 1)]
    [InlineData(ArchiveFilter.Including, 2)]
    [InlineData(ArchiveFilter.Only, 1)]
    public async Task Apply_selects_the_requested_part_of_the_stock(ArchiveFilter filter, int expected)
    {
        using var ctx = await TwoPeopleAsync();
        await using var db = ctx.NewContext();
        Assert.Equal(expected, await db.People.Apply(filter).CountAsync());
    }

    [Fact]
    public async Task SetArchivedAsync_writes_the_four_columns()
    {
        using var ctx = await TwoPeopleAsync();
        await using (var db = ctx.NewContext())
        {
            Assert.True(await RecordArchive.SetArchivedAsync<Person>(db, "aktiv", true, "  Aufgelöst  ", Actor));
        }
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == "aktiv");
            Assert.True(person.IsArchived);
            Assert.NotNull(person.ArchivedAt);
            Assert.Equal("agent-1", person.ArchivedById);
            Assert.Equal("Aufgelöst", person.ArchiveReason);
        }
    }

    [Fact]
    public async Task SetArchivedAsync_clears_the_columns_on_the_way_back()
    {
        using var ctx = await TwoPeopleAsync();
        await using (var db = ctx.NewContext())
        {
            Assert.True(await RecordArchive.SetArchivedAsync<Person>(db, "archiv", false, null, Actor));
        }
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == "archiv");
            Assert.False(person.IsArchived);
            Assert.Null(person.ArchivedAt);
            Assert.Null(person.ArchivedById);
            Assert.Null(person.ArchiveReason);
        }
    }

    [Fact]
    public async Task SetArchivedAsync_reports_no_change_when_the_state_already_matches()
    {
        using var ctx = await TwoPeopleAsync();
        await using var db = ctx.NewContext();
        Assert.False(await RecordArchive.SetArchivedAsync<Person>(db, "archiv", true, null, Actor));
    }

    [Fact]
    public async Task SetArchivedAsync_works_for_a_second_record_type()
    {
        using var ctx = await TwoPeopleAsync();
        await using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1"));
            await db.SaveChangesAsync();
            Assert.True(await RecordArchive.SetArchivedAsync<NOOSE_Website.Data.Entities.Factions.Faction>(
                db, "f1", true, null, Actor));
        }
        await using (var db = ctx.NewContext())
        {
            Assert.True((await db.Factions.SingleAsync(f => f.Id == "f1")).IsArchived);
        }
    }
}
