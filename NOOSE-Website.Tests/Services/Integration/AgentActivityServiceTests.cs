using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Activities;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Models.Activities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AgentActivityService"/> against in-memory SQLite.</summary>
public sealed class AgentActivityServiceTests
{
    // --- actors ---
    // Director: leadership.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, may write.
    private static ClaimsPrincipal LowRank(string id = "low")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // TeamLead without admin => read-only supervisor (fails RequireWriteAccess).
    private static ClaimsPrincipal ReadOnly(string id = "ro")
        => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().Build();

    private static AgentActivityService NewService(SqliteTestContext ctx, IThreatScoreService? threat = null)
        => new(ctx.Factory, threat ?? Substitute.For<IThreatScoreService>());

    // build an activity with links; CreatedById mirrors what the (absent) audit interceptor would stamp.
    private static AgentActivity Activity(string id, string title = "Aktivitaet", string? createdById = null,
        string? kind = null, DateTime? date = null, string contentHtml = "", bool deleted = false,
        (string type, string id)[]? links = null)
    {
        var a = new AgentActivity
        {
            Id = id,
            Title = title,
            Kind = kind,
            ActivityDate = date ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentHtml = contentHtml,
            CreatedById = createdById,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            IsDeleted = deleted,
            DeletedAt = deleted ? new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) : null,
        };
        foreach (var (type, targetId) in links ?? Array.Empty<(string, string)>())
        {
            a.Links.Add(new AgentActivityLink { TargetType = type, TargetId = targetId });
        }
        return a;
    }

    private static PersonGroup Group(string id, string name = "Gruppe", bool classified = false)
        => new()
        {
            Id = id,
            Name = name,
            CaseNumber = "FIB-G-2026-0001",
            IsClassified = classified,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    // ==================== GetListAsync ====================

    [Fact]
    public async Task GetListAsync_ReturnsActivities_NewestFirst_WithPlainContent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", title: "Alt",
                date: new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
                contentHtml: "<p>Hallo <b>Welt</b></p>"));
            db.AgentActivities.Add(Activity("a2", title: "Neu",
                date: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(Leader()));

        Assert.Equal(2, result.Count);
        Assert.Equal("a2", result[0].Id); // newest ActivityDate first
        var withHtml = result.Single(r => r.Id == "a1");
        Assert.Equal("Hallo Welt", withHtml.ContentPlain);
    }

    [Fact]
    public async Task GetListAsync_ShowsFactionOrgLink_ForLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.IsClassified = true));
            db.AgentActivities.Add(Activity("a1", links: new[] { (nameof(Faction), "f1") }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(Leader()));

        var org = Assert.Single(result[0].Orgs);
        Assert.Equal("Ballas", org.DisplayName);
    }

    [Fact]
    public async Task GetListAsync_HidesClassifiedFactionOrgLink_ForNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas", configure: f => f.IsClassified = true));
            db.AgentActivities.Add(Activity("a1", links: new[] { (nameof(Faction), "f1") }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(ViewerScope.From(LowRank()));

        Assert.Single(result);
        Assert.Empty(result[0].Orgs); // classified faction is not resolved for a non-leader
    }

    // ==================== GetDetailAsync ====================

    [Fact]
    public async Task GetDetailAsync_ReturnsDetail_WithOwnerName_AndVisibleOrgs()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: "me", configure: a => a.Codename = "Falke"));
            db.PersonGroups.Add(Group("g1", name: "Zelle A"));
            db.AgentActivities.Add(Activity("a1", createdById: "me", links: new[] { (nameof(PersonGroup), "g1") }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("a1", ViewerScope.From(Leader()));

        Assert.NotNull(result);
        Assert.Equal("Falke", result!.OwnerName);
        var org = Assert.Single(result.Orgs);
        Assert.Equal("Zelle A", org.DisplayName);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        Assert.Null(await svc.GetDetailAsync("nope", ViewerScope.From(Leader())));
    }

    // ==================== GetLinkedAsync ====================

    [Fact]
    public async Task GetLinkedAsync_ReturnsActivitiesLinkedToTarget()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.AgentActivities.Add(Activity("a1", links: new[] { (nameof(Faction), "f1") }));
            db.AgentActivities.Add(Activity("a2")); // unrelated
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetLinkedAsync(nameof(Faction), "f1", ViewerScope.From(Leader()));

        Assert.Single(result);
        Assert.Equal("a1", result[0].Id);
    }

    [Fact]
    public async Task GetLinkedAsync_ReturnsEmpty_WhenNoLinks()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.GetLinkedAsync(nameof(Faction), "f-none", ViewerScope.From(Leader()));

        Assert.Empty(result);
    }

    // ==================== GetLinkedFullAsync ====================

    [Fact]
    public async Task GetLinkedFullAsync_ReturnsFullActivities_WithHtml()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", contentHtml: "<p>Body</p>", links: new[] { (nameof(Faction), "f1") }));
            db.AgentActivities.Add(Activity("a2")); // unrelated
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetLinkedFullAsync(nameof(Faction), "f1");

        var activity = Assert.Single(result);
        Assert.Equal("a1", activity.Id);
        Assert.Equal("<p>Body</p>", activity.ContentHtml);
    }

    // ==================== GetTrashAsync ====================

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent(id: "me", configure: a => a.Codename = "Falke"));
            db.AgentActivities.Add(Activity("live", title: "Live"));
            db.AgentActivities.Add(Activity("dead", title: "Dead", createdById: "me", deleted: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetTrashAsync();

        Assert.Single(result);
        Assert.Equal("dead", result[0].Id);
        Assert.Equal("Falke", result[0].OwnerName);
    }

    // ==================== GetKindsAsync ====================

    [Fact]
    public async Task GetKindsAsync_ReturnsDistinctNonEmpty_Ordered()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", kind: "Streife"));
            db.AgentActivities.Add(Activity("a2", kind: "Aufklaerung"));
            db.AgentActivities.Add(Activity("a3", kind: "Streife")); // duplicate
            db.AgentActivities.Add(Activity("a4", kind: null));       // ignored
            db.AgentActivities.Add(Activity("a5", kind: ""));         // ignored
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetKindsAsync();

        Assert.Equal(new[] { "Aufklaerung", "Streife" }, result);
    }

    // ==================== CreateAsync ====================

    // creating is closed since deployments became operations; see DeploymentReportTests.ActivityCreate_IsClosed

    [Fact]
    public async Task CreateAsync_Throws_ForReadOnlyActor()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(new AgentActivityInput { Title = "Einsatz" }, ReadOnly()));
    }

    // ==================== UpdateAsync ====================

    [Fact]
    public async Task UpdateAsync_UpdatesFields_AddsAndRemovesLinks_AsCreator()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            db.Factions.Add(Seed.Faction(id: "f3", name: "Marabunta"));
            db.AgentActivities.Add(Activity("a1", title: "Alt", createdById: "me",
                links: new[] { (nameof(Faction), "f1"), (nameof(Faction), "f3") }));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);
        var input = new AgentActivityInput
        {
            Title = "Neu",
            Kind = "Streife",
            ActivityDate = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc),
            ContentHtml = "<p>x</p>",
            OrgLinks =
            {
                new AgentActivityOrgRef { TargetType = nameof(Faction), TargetId = "f1" }, // kept
                new AgentActivityOrgRef { TargetType = nameof(Faction), TargetId = "f2" }, // added
            },
        };

        // creator is a non-leadership junior agent.
        await svc.UpdateAsync("a1", input, LowRank("me"));

        using var check = ctx.NewContext();
        var stored = await check.AgentActivities.Include(a => a.Links).SingleAsync(a => a.Id == "a1");
        Assert.Equal("Neu", stored.Title);
        Assert.Equal("Streife", stored.Kind);
        Assert.Equal(new HashSet<string> { "f1", "f2" }, stored.Links.Select(l => l.TargetId).ToHashSet());
        // recompute covers both the previously-linked and the now-linked factions.
        await threat.Received().NewCalculateAsync("f3", Arg.Any<CancellationToken>());
        await threat.Received().NewCalculateAsync("f2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", createdById: "me"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateAsync("a1", new AgentActivityInput { Title = "Neu" }, LowRank("stranger")));
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenEmptyTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", createdById: "me"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("a1", new AgentActivityInput { Title = "  " }, LowRank("me")));
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("nope", new AgentActivityInput { Title = "Neu" }, Leader()));
    }

    // ==================== DeleteAsync ====================

    [Fact]
    public async Task DeleteAsync_RemovesActivity_AndRecomputesFactions_AsLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", links: new[] { (nameof(Faction), "f1") }));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.DeleteAsync("a1", Leader());

        // interceptor absent in tests => hard delete; row gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.AgentActivities.AnyAsync(a => a.Id == "a1"));
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", createdById: "me"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("a1", LowRank("stranger")));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.DeleteAsync("nope", Leader()));
    }

    // ==================== RestoreAsync ====================

    [Fact]
    public async Task RestoreAsync_ClearsDeletedFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", deleted: true, links: new[] { (nameof(Faction), "f1") }));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.RestoreAsync("a1", Leader());

        using var check = ctx.NewContext();
        var restored = await check.AgentActivities.IgnoreQueryFilters().SingleAsync(a => a.Id == "a1");
        Assert.False(restored.IsDeleted);
        Assert.Null(restored.DeletedAt);
        Assert.Null(restored.DeletedById);
        await threat.Received().NewCalculateAsync("f1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentActivities.Add(Activity("a1", createdById: "me", deleted: true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // even the creator cannot restore; leadership guard runs first.
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("a1", LowRank("me")));
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenMissing()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync("nope", Leader()));
    }
}
