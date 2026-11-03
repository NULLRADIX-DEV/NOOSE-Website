using System.Security.Claims;
using System.Text.Json;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary><see cref="TagService.AddManyAsync"/>: one tag set added to many picked records.</summary>
public sealed class TagServiceBatchTests
{
    private static ClaimsPrincipal Lead() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static void SeedBase(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.People.Add(Seed.Person("p1", "Eins"));
        db.People.Add(Seed.Person("p2", "Zwei"));
        db.Cases.Add(Seed.Case("c1"));
        db.Tags.Add(new Tag { Id = "t-alt", Name = "Alt" });
        db.Tags.Add(new Tag { Id = "t-gang", Name = "Gang" });
        db.Tags.Add(new Tag { Id = "t-waffen", Name = "Waffen" });
        // p1 already carries one of the two new tags and one unrelated tag
        db.TagMappings.Add(new TagMapping { TagId = "t-alt", EntityType = "Person", EntityId = "p1" });
        db.TagMappings.Add(new TagMapping { TagId = "t-gang", EntityType = "Person", EntityId = "p1" });
        db.SaveChanges();
    }

    /// <summary>The new value of the "Tags hinzugefügt" change, in the {field:[old,new]} shape.</summary>
    private static string? Added(string? changesJson)
    {
        var changes = JsonSerializer.Deserialize<Dictionary<string, string?[]>>(changesJson!)!;
        Assert.Equal(["Tags hinzugefügt"], changes.Keys);
        return changes["Tags hinzugefügt"][1];
    }

    [Fact]
    public async Task Adds_what_is_missing_and_never_takes_a_tag_away()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var factory = new CountingDbContextFactory(ctx);
        var svc = new TagService(factory);

        var outcome = await svc.AddManyAsync([("Person", "p1"), ("Person", "p2"), ("Case", "c1")], ["t-gang", "t-waffen"], Lead());

        Assert.Equal(new(3, 0, 0), outcome);
        Assert.Equal(1, factory.Saves);
        using var db = ctx.NewContext();
        Assert.Equal(["t-alt", "t-gang", "t-waffen"],
            db.TagMappings.Where(m => m.EntityId == "p1").Select(m => m.TagId).OrderBy(t => t).ToList());
        Assert.Equal(["t-gang", "t-waffen"],
            db.TagMappings.Where(m => m.EntityId == "p2").Select(m => m.TagId).OrderBy(t => t).ToList());
        Assert.Equal(2, db.TagMappings.Count(m => m.EntityType == "Case" && m.EntityId == "c1"));
    }

    [Fact]
    public async Task Each_changed_record_gets_one_audit_row_naming_only_the_new_tags()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var svc = new TagService(ctx.Factory);

        await svc.AddManyAsync([("Person", "p1"), ("Person", "p2")], ["t-gang", "t-waffen"], Lead());

        using var db = ctx.NewContext();
        var p1 = Assert.Single(db.AuditLogs.Where(a => a.EntityType == "Person" && a.EntityId == "p1"));
        Assert.Equal("Waffen", Added(p1.ChangesJson));
        var p2 = Assert.Single(db.AuditLogs.Where(a => a.EntityType == "Person" && a.EntityId == "p2"));
        Assert.Equal("Gang, Waffen", Added(p2.ChangesJson));
        Assert.Equal(AuditAction.Modified, p2.Action);
    }

    [Fact]
    public async Task A_record_that_has_them_all_is_unchanged_and_logs_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var factory = new CountingDbContextFactory(ctx);
        var svc = new TagService(factory);

        var outcome = await svc.AddManyAsync([("Person", "p1")], ["t-gang"], Lead());

        Assert.Equal(new(0, 1, 0), outcome);
        Assert.Equal(0, factory.Saves);
        using var db = ctx.NewContext();
        Assert.Empty(db.AuditLogs);
    }

    [Fact]
    public async Task Types_the_tag_filter_does_not_know_and_hidden_records_are_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        using (var db = ctx.NewContext())
        {
            db.Documents.Add(new Document { Id = "doc1", Title = "Bericht" });
            db.Appointments.Add(new Appointment { Id = "a1", Title = "Treffen" });
            db.Laws.Add(new Law { Id = "l1", Title = "Paragraf 1" });
            db.People.Add(Seed.Person("secret", "Geheim", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = new TagService(ctx.Factory);

        var outcome = await svc.AddManyAsync(
            [("Document", "doc1"), ("Appointment", "a1"), ("Law", "l1"), ("Person", "secret"), ("Person", "missing"), ("Person", "p2")],
            ["t-waffen"], Junior());

        Assert.Equal(new(1, 0, 5), outcome);
        using var check = ctx.NewContext();
        Assert.Equal("p2", Assert.Single(check.TagMappings.Where(m => m.TagId == "t-waffen")).EntityId);
    }

    [Fact]
    public async Task Unknown_tags_drop_out_and_none_left_is_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var svc = new TagService(ctx.Factory);

        var outcome = await svc.AddManyAsync([("Person", "p2")], ["t-waffen", "gibt-es-nicht"], Lead());
        Assert.Equal(new(1, 0, 0), outcome);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AddManyAsync([("Person", "p2")], ["gibt-es-nicht"], Lead()));
    }

    [Theory]
    [InlineData("reader")]
    [InlineData("partner")]
    [InlineData("demo")]
    public async Task A_read_only_actor_is_stopped_before_anything_is_written(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var factory = new CountingDbContextFactory(ctx);
        var svc = new TagService(factory);
        ClaimsPrincipal actor = kind switch
        {
            "reader" => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsTeamLead().Build(),
            "partner" => ClaimsPrincipalBuilder.Agent("lead").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
            _ => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsDemo().Build(),
        };

        // the write guard, not a visibility refusal further down
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AddManyAsync([("Person", "p2")], ["t-waffen"], actor));
        Assert.StartsWith("Nur-Lese-Modus", ex.Message);
        Assert.Equal(0, factory.Saves);
    }

    [Fact]
    public async Task Too_many_records_are_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedBase(ctx);
        var svc = new TagService(ctx.Factory);
        var many = Enumerable.Range(0, RecordBatch.Max + 1).Select(i => ("Person", $"p{i}")).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AddManyAsync(many, ["t-waffen"], Lead()));
    }
}
