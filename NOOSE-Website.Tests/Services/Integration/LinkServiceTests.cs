using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="LinkService"/> against in-memory SQLite.</summary>
public sealed class LinkServiceTests
{
    private static (LinkService svc, IThreatScoreService threat) NewService(SqliteTestContext ctx)
    {
        var threat = Substitute.For<IThreatScoreService>();
        return (new LinkService(ctx.Factory, threat), threat);
    }

    // Director => IsLeadership => MayClassifiedRead => full read scope.
    private static ViewerScope LeaderScope(string meId = "me")
        => ViewerScope.From(ClaimsPrincipalBuilder.Agent(meId).WithRank(Rank.Director).Build());

    // JuniorAgent: not leadership, cannot read classified.
    private static ViewerScope JuniorScope(string meId = "me")
        => ViewerScope.From(ClaimsPrincipalBuilder.Agent(meId).WithRank(Rank.JuniorAgent).Build());

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal NonLeader()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

    // ---- GetForRecordAsync (scope overload) --------------------------------

    [Fact]
    public async Task GetForRecordAsync_ReturnsOtherSide_WhenViewedRecordIsSource()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person("p1", "Max");
        var faction = Seed.Faction("f1", "Ballas");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.Factions.Add(faction);
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        var link = Assert.Single(result);
        Assert.Equal("Faction", link.OtherType);
        Assert.Equal("f1", link.OtherId);
        Assert.Equal($"{faction.Name} ({faction.CaseNumber})", link.OtherDesignation);
        Assert.Equal("/fraktionen/f1", link.Href);
    }

    [Fact]
    public async Task GetForRecordAsync_NormalizesBidirectionally_WhenViewedRecordIsTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            // link stored Faction -> Person; viewing the Person must surface the Faction as "other side".
            db.Links.Add(new Link { SourceType = "Faction", SourceId = "f1", TargetType = "Person", TargetId = "p1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        var link = Assert.Single(result);
        Assert.Equal("Faction", link.OtherType);
        Assert.Equal("f1", link.OtherId);
    }

    [Fact]
    public async Task GetForRecordAsync_FiltersByKind()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Factions.Add(Seed.Faction("f2", "Vagos"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1", Kind = LinkKind.Conflict });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f2", Kind = LinkKind.Alliance });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope(), LinkKind.Conflict);

        var link = Assert.Single(result);
        Assert.Equal("f1", link.OtherId);
    }

    [Fact]
    public async Task GetForRecordAsync_ReturnsEmpty_WhenViewedRecordNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // viewed record itself is classified -> a junior viewer cannot see it at all.
            db.People.Add(Seed.Person("p1", "Max", p => p.IsClassified = true));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", JuniorScope());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_HidesClassifiedOtherSide_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // viewed record is visible, but the linked faction is classified.
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.IsClassified = true));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", JuniorScope());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_ShowsClassifiedOtherSide_ToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.IsClassified = true));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        var link = Assert.Single(result);
        Assert.Equal("f1", link.OtherId);
    }

    [Fact]
    public async Task GetForRecordAsync_HidesUnresolvedKnownType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            // target faction id does not exist -> known type but unresolved -> hidden.
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "ghost" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_ReturnsRawLabel_ForUnknownType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            // unknown entity type -> raw label, no nav target.
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Widget", TargetId = "w1", Label = "gizmo" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        var link = Assert.Single(result);
        Assert.Equal("Widget", link.OtherType);
        Assert.Equal("w1", link.OtherId);
        Assert.Equal("w1", link.OtherDesignation);
        Assert.Equal("gizmo", link.Label);
        Assert.Null(link.Href);
    }

    [Fact]
    public async Task GetForRecordAsync_OrdersByCreatedAtDescending()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Factions.Add(Seed.Faction("f2", "Vagos"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f2", CreatedAt = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", LeaderScope());

        Assert.Equal(2, result.Count);
        // newest first.
        Assert.Equal("f2", result[0].OtherId);
        Assert.Equal("f1", result[1].OtherId);
    }

    // ---- GetForRecordAsync (bool/meId overload) ----------------------------

    [Fact]
    public async Task GetForRecordAsync_BoolOverload_ReturnsLinks()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", isLeadership: true, meId: "me");

        var link = Assert.Single(result);
        Assert.Equal("f1", link.OtherId);
    }

    // ---- restricted jobs ---------------------------------------------------

    // Person p1 linked to a restricted job that only its creator and "assignee" may see.
    private static void SeedPersonLinkedToRestrictedJob(SqliteTestContext ctx)
    {
        using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("creator"));
        db.Users.Add(Seed.Agent("assignee"));
        db.Users.Add(Seed.Agent("outsider"));
        db.People.Add(Seed.Person("p1", "Max"));
        db.Jobs.Add(new Job { Id = "j1", Title = "Zugriff Lager", CaseNumber = "NOOSE-A-2026-0001", IsRestricted = true, CreatedById = "creator" });
        db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "assignee" });
        db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Job", TargetId = "j1" });
        db.SaveChanges();
    }

    [Fact]
    public async Task GetForRecordAsync_HidesRestrictedJob_FromUninvolvedAgent()
    {
        using var ctx = new SqliteTestContext();
        SeedPersonLinkedToRestrictedJob(ctx);
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", JuniorScope("outsider"));

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForRecordAsync_ShowsRestrictedJob_ToAssignee()
    {
        using var ctx = new SqliteTestContext();
        SeedPersonLinkedToRestrictedJob(ctx);
        var (svc, _) = NewService(ctx);

        var result = await svc.GetForRecordAsync("Person", "p1", JuniorScope("assignee"));

        var link = Assert.Single(result);
        Assert.Equal("j1", link.OtherId);
        Assert.Equal("Zugriff Lager (NOOSE-A-2026-0001)", link.OtherDesignation);
        Assert.Equal("/aufgaben/j1", link.Href);
    }

    // ---- CreateAsync -------------------------------------------------------

    [Fact]
    public async Task CreateAsync_PersistsLink_AndTrimsLabel()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.CreateAsync("Person", "p1", "Faction", "f1", "  wichtig  ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Links.SingleAsync();
        Assert.Equal("Person", stored.SourceType);
        Assert.Equal("p1", stored.SourceId);
        Assert.Equal("Faction", stored.TargetType);
        Assert.Equal("f1", stored.TargetId);
        Assert.Equal("wichtig", stored.Label);
        Assert.Equal(LinkKind.Default, stored.Kind);
    }

    [Fact]
    public async Task CreateAsync_NullsBlankLabel()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.CreateAsync("Person", "p1", "Faction", "f1", "   ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Links.SingleAsync();
        Assert.Null(stored.Label);
    }

    [Fact]
    public async Task CreateAsync_PersistsKind()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.CreateAsync("Person", "p1", "Faction", "f1", null, Leader(), LinkKind.Conflict);

        using var check = ctx.NewContext();
        var stored = await check.Links.SingleAsync();
        Assert.Equal(LinkKind.Conflict, stored.Kind);
    }

    [Fact]
    public async Task CreateAsync_RecomputesThreat_ForInvolvedFactionAndPerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            db.People.Add(Seed.Person("p1", "Max"));
            db.SaveChanges();
        }
        var (svc, threat) = NewService(ctx);

        await svc.CreateAsync("Faction", "f1", "Person", "p1", null, Leader());

        await threat.Received(1).NewCalculateAsync("f1");
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task CreateAsync_Throws_OnSelfLink()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Person", "p1", "Person", "p1", null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_OnDuplicate_EitherDirection()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas"));
            // existing link Person -> Faction.
            db.Links.Add(new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        // creating the reverse direction of the same kind must be rejected.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("Faction", "f1", "Person", "p1", null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTargetClassified_AndActorNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Max"));
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "p1", "Faction", "f1", null, NonLeader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTargetIsRestrictedJob_TheActorCannotSee()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator"));
            db.People.Add(Seed.Person("p1", "Max"));
            db.Jobs.Add(new Job { Id = "j1", Title = "Zugriff Lager", CaseNumber = "NOOSE-A-2026-0001", IsRestricted = true, CreatedById = "creator" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        // forged call: the picker never offers this job to the actor
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("Person", "p1", "Job", "j1", null, NonLeader()));

        using var check = ctx.NewContext();
        Assert.False(await check.Links.AnyAsync());
    }

    [Fact]
    public async Task CreateAsync_LinksRestrictedJob_ForAssignee()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator"));
            db.Users.Add(Seed.Agent("assignee"));
            db.People.Add(Seed.Person("p1", "Max"));
            db.Jobs.Add(new Job { Id = "j1", Title = "Zugriff Lager", CaseNumber = "NOOSE-A-2026-0001", IsRestricted = true, CreatedById = "creator" });
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "assignee" });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);
        var assignee = ClaimsPrincipalBuilder.Agent("assignee").WithRank(Rank.JuniorAgent).Build();

        await svc.CreateAsync("Person", "p1", "Job", "j1", null, assignee);

        using var check = ctx.NewContext();
        Assert.Equal("j1", (await check.Links.SingleAsync()).TargetId);
    }

    // ---- UpdateLabelAsync --------------------------------------------------

    [Fact]
    public async Task UpdateLabelAsync_SetsLabel_Trimmed()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.UpdateLabelAsync(link.Id, "  Zeuge  ", Leader());

        using var check = ctx.NewContext();
        Assert.Equal("Zeuge", (await check.Links.SingleAsync(x => x.Id == link.Id)).Label);
    }

    [Fact]
    public async Task UpdateLabelAsync_OverwritesExistingLabel()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1", Label = "Zeuge" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.UpdateLabelAsync(link.Id, "Beschuldigter", Leader());

        using var check = ctx.NewContext();
        Assert.Equal("Beschuldigter", (await check.Links.SingleAsync(x => x.Id == link.Id)).Label);
    }

    [Fact]
    public async Task UpdateLabelAsync_NullsBlankLabel()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1", Label = "Zeuge" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        await svc.UpdateLabelAsync(link.Id, "   ", Leader());

        using var check = ctx.NewContext();
        Assert.Null((await check.Links.SingleAsync(x => x.Id == link.Id)).Label);
    }

    [Fact]
    public async Task UpdateLabelAsync_NoOp_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Case", SourceId = "c1", TargetType = "Person", TargetId = "p1", Label = "Zeuge" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        // returns without throwing; existing link untouched.
        await svc.UpdateLabelAsync("missing", "Beschuldigter", Leader());

        using var check = ctx.NewContext();
        Assert.Equal("Zeuge", (await check.Links.SingleAsync(x => x.Id == link.Id)).Label);
    }

    // ---- RemoveAsync -------------------------------------------------------

    [Fact]
    public async Task RemoveAsync_DeletesLink_AndRecomputesThreat()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Faction", SourceId = "f1", TargetType = "Person", TargetId = "p1" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, threat) = NewService(ctx);

        await svc.RemoveAsync(link.Id, Leader());

        using var check = ctx.NewContext();
        // no soft-delete interceptor in the test context -> hard delete.
        Assert.False(await check.Links.AnyAsync(x => x.Id == link.Id));
        await threat.Received(1).NewCalculateAsync("f1");
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task RemoveAsync_NoOp_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var link = new Link { SourceType = "Person", SourceId = "p1", TargetType = "Faction", TargetId = "f1" };
        using (var db = ctx.NewContext())
        {
            db.Links.Add(link);
            db.SaveChanges();
        }
        var (svc, threat) = NewService(ctx);

        // returns without throwing; existing link untouched.
        await svc.RemoveAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.True(await check.Links.AnyAsync(x => x.Id == link.Id));
        await threat.DidNotReceive().NewCalculateAsync(Arg.Any<string>());
    }
}
