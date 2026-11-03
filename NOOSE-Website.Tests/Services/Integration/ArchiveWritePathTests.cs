using System.Security.Claims;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Archiving is a write, is audited, and must not look like an edit.</summary>
public sealed class ArchiveWritePathTests
{
    private static PersonService Build(SqliteTestContext ctx)
        => new(ctx.Factory,
            Substitute.For<IFileStorageService>(),
            Substitute.For<IProfileSuggestionService>(),
            Substitute.For<ICaseNumberService>(),
            Substitute.For<IThreatScoreService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<NOOSE_Website.Services.Public.IPublicWantedService>());

    private static ClaimsPrincipal Junior(string id = "junior")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static async Task<SqliteTestContext> OnePersonAsync(DateTime? modifiedAt = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("junior", Rank.Trainee));
        db.People.Add(Seed.Person("p1", "Akte", p => p.ModifiedAt = modifiedAt));
        await db.SaveChangesAsync();
        return ctx;
    }

    [Fact]
    public async Task ArchiveAsync_sets_the_flag_and_keeps_the_record_readable()
    {
        using var ctx = await OnePersonAsync();
        await Build(ctx).ArchiveAsync("p1", "Aufgelöst", Junior());

        await using var db = ctx.NewContext();
        var person = await db.People.SingleAsync(p => p.Id == "p1");
        Assert.True(person.IsArchived);
        Assert.Equal("Aufgelöst", person.ArchiveReason);
        Assert.Equal("junior", person.ArchivedById);
    }

    [Fact]
    public async Task ArchiveAsync_leaves_the_edit_stamp_alone()
    {
        var edited = new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);
        using var ctx = await OnePersonAsync(edited);
        await Build(ctx).ArchiveAsync("p1", null, Junior());

        await using var db = ctx.NewContext();
        Assert.Equal(edited, (await db.People.SingleAsync(p => p.Id == "p1")).ModifiedAt);
    }

    [Fact]
    public async Task ArchiveAsync_writes_an_audit_row_against_the_record()
    {
        using var ctx = await OnePersonAsync();
        await Build(ctx).ArchiveAsync("p1", "Aufgelöst", Junior());

        await using var db = ctx.NewContext();
        var row = await db.AuditLogs.SingleAsync(a => a.EntityType == nameof(Person) && a.EntityId == "p1");
        Assert.Equal(AuditAction.Archived, row.Action);
        Assert.Equal("junior", row.AgentId);
        // the audit viewer only renders the {field:[old,new]} shape, so assert on the parsed value
        var changes = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, string?[]>>(row.ChangesJson!)!;
        Assert.Equal("Aufgelöst", changes["Archivgrund"][1]);
    }

    [Fact]
    public async Task UnarchiveAsync_clears_the_flag_and_audits_its_own_action()
    {
        using var ctx = await OnePersonAsync();
        var service = Build(ctx);
        await service.ArchiveAsync("p1", null, Junior());
        await service.UnarchiveAsync("p1", Junior());

        await using var db = ctx.NewContext();
        Assert.False((await db.People.SingleAsync(p => p.Id == "p1")).IsArchived);
        Assert.Contains(await db.AuditLogs.ToListAsync(), a => a.Action == AuditAction.Unarchived);
    }

    [Fact]
    public async Task Archiving_twice_writes_only_one_audit_row()
    {
        using var ctx = await OnePersonAsync();
        var service = Build(ctx);
        await service.ArchiveAsync("p1", null, Junior());
        await service.ArchiveAsync("p1", null, Junior());

        await using var db = ctx.NewContext();
        Assert.Equal(1, await db.AuditLogs.CountAsync(a => a.Action == AuditAction.Archived));
    }

    [Theory]
    [InlineData("readonly")]
    [InlineData("partner")]
    [InlineData("demo")]
    public async Task Accounts_without_write_access_are_refused(string kind)
    {
        using var ctx = await OnePersonAsync();
        ClaimsPrincipal actor = kind switch
        {
            "readonly" => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build(),
            "partner" => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Trainee)
                .AsPartner(PartnerAgency.DoJ, PartnerRank.Member).Build(),
            _ => ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build(),
        };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).ArchiveAsync("p1", null, actor));
    }

    [Fact]
    public async Task An_agent_cannot_archive_a_record_they_may_not_see()
    {
        using var ctx = await OnePersonAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == "p1");
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }

        // hiding a record from everyone must not be reachable for someone who cannot open it
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => Build(ctx).ArchiveAsync("p1", null, Junior()));

        await using (var db = ctx.NewContext())
        {
            Assert.False((await db.People.SingleAsync(p => p.Id == "p1")).IsArchived);
        }
    }

    [Fact]
    public async Task Leadership_may_archive_a_classified_record()
    {
        using var ctx = await OnePersonAsync();
        await using (var db = ctx.NewContext())
        {
            var person = await db.People.SingleAsync(p => p.Id == "p1");
            person.IsClassified = true;
            await db.SaveChangesAsync();
        }

        var lead = ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
        await Build(ctx).ArchiveAsync("p1", null, lead);

        await using (var db = ctx.NewContext())
        {
            Assert.True((await db.People.SingleAsync(p => p.Id == "p1")).IsArchived);
        }
    }
}
