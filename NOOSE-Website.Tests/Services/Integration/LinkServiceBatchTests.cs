using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary><see cref="LinkService.CreateManyAsync"/>: many picked records linked to one anchor from the search page.</summary>
public sealed class LinkServiceBatchTests
{
    private static ClaimsPrincipal Lead() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static (LinkService Svc, IThreatScoreService Threat, CountingDbContextFactory Factory) NewService(SqliteTestContext ctx)
    {
        var threat = Substitute.For<IThreatScoreService>();
        var factory = new CountingDbContextFactory(ctx);
        return (new LinkService(factory, threat), threat, factory);
    }

    private static void SeedCaseAndPeople(SqliteTestContext ctx, params string[] people)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("junior", Rank.Trainee));
        db.Cases.Add(Seed.Case("c1"));
        foreach (var p in people)
        {
            db.People.Add(Seed.Person(p, "Person " + p));
        }
        db.SaveChanges();
    }

    [Fact]
    public async Task Links_every_pick_with_the_anchor_as_source_in_one_save()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1", "p2", "p3");
        var (svc, _, factory) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1", [("Person", "p1"), ("Person", "p2"), ("Person", "p3")],
            "  Beschuldigter  ", Lead());

        Assert.Equal(new(3, 0, 0), outcome);
        Assert.Equal(1, factory.Saves);
        using var db = ctx.NewContext();
        var links = db.Links.OrderBy(l => l.TargetId).ToList();
        Assert.Equal(["p1", "p2", "p3"], links.Select(l => l.TargetId));
        Assert.All(links, l =>
        {
            Assert.Equal("Case", l.SourceType);
            Assert.Equal("c1", l.SourceId);
            Assert.Equal(LinkKind.Default, l.Kind);
            Assert.False(l.Automatic);
            Assert.Equal("Beschuldigter", l.Label);
        });
    }

    [Fact]
    public async Task An_existing_link_either_way_is_left_alone()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1", "p2", "p3", "p4");
        using (var db = ctx.NewContext())
        {
            db.Links.Add(new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1" });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p2", TargetType = "Case", TargetId = "c1" });
            // a system link counts as linked too, as it does for the single path
            db.Links.Add(new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p3", Automatic = true });
            db.SaveChanges();
        }
        var (svc, _, _) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1",
            [("Person", "p1"), ("Person", "p2"), ("Person", "p3"), ("Person", "p4")], null, Lead());

        Assert.Equal(new(1, 3, 0), outcome);
        using var check = ctx.NewContext();
        Assert.Equal(4, check.Links.Count());
    }

    [Fact]
    public async Task A_removed_link_and_a_link_of_another_kind_do_not_count()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1", "p2");
        using (var db = ctx.NewContext())
        {
            db.Links.Add(new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1", IsDeleted = true });
            db.Links.Add(new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p2", Kind = LinkKind.Conflict });
            db.SaveChanges();
        }
        var (svc, _, _) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1", [("Person", "p1"), ("Person", "p2")], null, Lead());

        Assert.Equal(new(2, 0, 0), outcome);
    }

    [Fact]
    public async Task The_anchor_itself_is_refused_and_the_rest_goes_through()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        var (svc, _, _) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1", [("Case", "c1"), ("Person", "p1")], null, Lead());

        Assert.Equal(new(1, 0, 1), outcome);
    }

    [Fact]
    public async Task What_the_actor_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "open");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("secret", "Geheim", p => p.IsClassified = true));
            db.Taskforces.Add(new Taskforce { Id = "tf1", Name = "Fremd", CaseNumber = "FIB-TF-2026-0001" });
            db.Jobs.Add(new Job { Id = "j1", Title = "Intern", CaseNumber = "FIB-A-2026-0001", IsRestricted = true, CreatedById = "someone" });
            db.SaveChanges();
        }
        var (svc, _, _) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1",
            [("Person", "open"), ("Person", "secret"), ("Taskforce", "tf1"), ("Job", "j1"), ("Person", "missing")], null, Junior());

        Assert.Equal(new(1, 0, 4), outcome);
        using var check = ctx.NewContext();
        Assert.Equal("open", Assert.Single(check.Links).TargetId);
    }

    [Fact]
    public async Task An_operation_takes_only_people_and_organisations()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(new Operation { Id = "o1", Title = "Nachtfalke", CaseNumber = "FIB-OP-2026-0001" });
            db.SaveChanges();
        }
        var (svc, _, _) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Operation", "o1", [("Person", "p1"), ("Case", "c1")], null, Lead());

        Assert.Equal(new(1, 0, 1), outcome);
    }

    [Theory]
    [InlineData("Faction", "f1")]
    [InlineData("PersonDoc", "d1")]
    [InlineData("Hinweis", "h1")]
    [InlineData("Case", "missing")]
    public async Task An_anchor_that_is_no_anchor_or_not_there_writes_nothing(string type, string id)
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1"));
            db.SaveChanges();
        }
        var (svc, threat, factory) = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateManyAsync(type, id, [("Person", "p1")], null, Lead()));

        Assert.Equal(0, factory.Saves);
        await threat.DidNotReceiveWithAnyArgs().NewCalculatePersonScoreAsync(default!, default);
    }

    [Fact]
    public async Task A_classified_anchor_is_refused_to_a_junior()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Seed.Case("vs", "Verschlusssache", c => c.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _, factory) = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateManyAsync("Case", "vs", [("Person", "p1")], null, Junior()));
        Assert.Equal(0, factory.Saves);
    }

    public static TheoryData<string> ReadOnlyActors => ["reader", "partner", "demo"];

    private static ClaimsPrincipal ReadOnly(string kind) => kind switch
    {
        "reader" => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsTeamLead().Build(),
        "partner" => ClaimsPrincipalBuilder.Agent("lead").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
        _ => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsDemo().Build(),
    };

    [Theory]
    [MemberData(nameof(ReadOnlyActors))]
    public async Task A_read_only_actor_is_stopped_before_anything_is_written(string kind)
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        var (svc, threat, factory) = NewService(ctx);

        // the write guard, not a visibility refusal further down
        var ex = await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateManyAsync("Case", "c1", [("Person", "p1")], null, ReadOnly(kind)));
        Assert.StartsWith("Nur-Lese-Modus", ex.Message);

        Assert.Equal(0, factory.Saves);
        await threat.DidNotReceiveWithAnyArgs().NewCalculatePersonScoreAsync(default!, default);
    }

    [Fact]
    public async Task Too_many_records_or_too_long_a_label_is_refused()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        var (svc, _, factory) = NewService(ctx);
        var many = Enumerable.Range(0, RecordBatch.Max + 1).Select(i => ("Person", $"p{i}")).ToList();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateManyAsync("Case", "c1", many, null, Lead()));
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateManyAsync("Case", "c1", [("Person", "p1")], new string('x', 201), Lead()));
        Assert.Equal(0, factory.Saves);
    }

    [Fact]
    public async Task Each_touched_person_is_scored_once()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "anchor", "p1", "p2", "p3");
        var (svc, threat, _) = NewService(ctx);

        await svc.CreateManyAsync("Person", "anchor", [("Person", "p1"), ("Person", "p2"), ("Person", "p3")], null, Lead());

        await threat.Received(1).NewCalculatePersonScoreAsync("anchor", Arg.Any<CancellationToken>());
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
        await threat.Received(4).NewCalculatePersonScoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_score_keeps_the_links()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1", "p2");
        var (svc, threat, _) = NewService(ctx);
        threat.NewCalculatePersonScoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("kaputt")));

        var outcome = await svc.CreateManyAsync("Case", "c1", [("Person", "p1"), ("Person", "p2")], null, Lead());

        Assert.Equal(2, outcome.Done);
        using var check = ctx.NewContext();
        Assert.Equal(2, check.Links.Count());
    }

    [Fact]
    public async Task Nothing_new_saves_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedCaseAndPeople(ctx, "p1");
        using (var db = ctx.NewContext())
        {
            db.Links.Add(new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1" });
            db.SaveChanges();
        }
        var (svc, threat, factory) = NewService(ctx);

        var outcome = await svc.CreateManyAsync("Case", "c1", [("Person", "p1")], null, Lead());

        Assert.Equal(new(0, 1, 0), outcome);
        Assert.Equal(0, factory.Saves);
        await threat.DidNotReceiveWithAnyArgs().NewCalculatePersonScoreAsync(default!, default);
    }
}
