using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="RelationService"/> against in-memory SQLite.</summary>
public sealed class RelationServiceTests
{
    // Rank >= SpecialAgentInCharge(4) or admin => leadership => MayClassifiedRead.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Junior agent: not leadership, cannot read classified.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    // Read-only supervision (IsTeamLead && !IsAdmin) => fails RequireWriteAccess.
    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("reader").WithRank(Rank.Director).AsTeamLead().Build();

    private static ViewerScope LeaderScope() => ViewerScope.From(Leader());
    private static ViewerScope PlainScope() => ViewerScope.From(Junior());

    private static RelationService NewService(SqliteTestContext ctx, IThreatScoreService? threat = null)
        => new(ctx.Factory, threat ?? Substitute.For<IThreatScoreService>(), Substitute.For<INotificationService>());

    private static PersonRelation NewRelation(string id, string aId, string bId, RelationType type,
        DateTime createdAt, string? note = null)
        => new()
        {
            Id = id,
            PersonAId = aId,
            PersonBId = bId,
            Type = type,
            Note = note,
            CreatedAt = createdAt,
        };

    private static DateTime At(int day) => new(2026, 1, day, 0, 0, 0, DateTimeKind.Utc);

    // ---- GetForPersonAsync ----

    [Fact]
    public async Task GetForPersonAsync_ReturnsRelations_FromEitherStoredSide_OrderedByCreatedDescending()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Subject"));
            db.People.Add(Seed.Person("p2", "Partner B"));
            db.People.Add(Seed.Person("p3", "Partner A"));
            // p1 stored as PersonA in one, PersonB in the other.
            db.PersonRelations.Add(NewRelation("r-old", "p1", "p2", RelationType.Ally, At(1), "note"));
            db.PersonRelations.Add(NewRelation("r-new", "p3", "p1", RelationType.Enemy, At(5)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetForPersonAsync("p1", LeaderScope());

        Assert.Equal(new[] { "r-new", "r-old" }, result.Select(r => r.RelationId).ToArray());
        Assert.Equal(new[] { "p3", "p2" }, result.Select(r => r.OtherPersonId).ToArray());
        var old = result.Single(r => r.RelationId == "r-old");
        Assert.Equal("Partner B", old.OtherPersonName);
        Assert.Equal(RelationType.Ally, old.Type);
        Assert.Equal("note", old.Note);
    }

    [Fact]
    public async Task GetForPersonAsync_HidesClassifiedOther_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Subject"));
            db.People.Add(Seed.Person("open", "Open"));
            db.People.Add(Seed.Person("secret", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonRelations.Add(NewRelation("r-open", "p1", "open", RelationType.Known, At(1)));
            db.PersonRelations.Add(NewRelation("r-secret", "p1", "secret", RelationType.Known, At(2)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetForPersonAsync("p1", PlainScope());

        Assert.Equal(new[] { "r-open" }, result.Select(r => r.RelationId).ToArray());
    }

    [Fact]
    public async Task GetForPersonAsync_ShowsClassifiedOther_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Subject"));
            db.People.Add(Seed.Person("secret", "Secret", p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonRelations.Add(NewRelation("r-secret", "p1", "secret", RelationType.Known, At(2)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetForPersonAsync("p1", LeaderScope());

        Assert.Equal(new[] { "r-secret" }, result.Select(r => r.RelationId).ToArray());
    }

    [Fact]
    public async Task GetForPersonAsync_SkipsRelation_WhenOtherPersonTrashed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Subject"));
            db.People.Add(Seed.Person("gone", "Gone", p => p.IsDeleted = true));
            db.PersonRelations.Add(NewRelation("r-gone", "p1", "gone", RelationType.Known, At(2)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // soft-delete query filter nulls the included counterpart => relation skipped.
        var result = await svc.GetForPersonAsync("p1", LeaderScope());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForPersonAsync_Partner_ReturnsOnlyReleasedOthers()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Subject"));
            db.People.Add(Seed.Person("shared", "Shared"));
            db.People.Add(Seed.Person("hidden", "Hidden"));
            db.PersonRelations.Add(NewRelation("r-shared", "p1", "shared", RelationType.Known, At(2)));
            db.PersonRelations.Add(NewRelation("r-hidden", "p1", "hidden", RelationType.Known, At(1)));
            // only "shared" is released to the DoJ.
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person), EntityId = "shared", Agency = PartnerAgency.DoJ, CreatedAt = At(1),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var partnerScope = new ViewerScope(
            MayClassifiedRead: false, MayAllTaskforces: false, MeId: "partner-1", PartnerAgency: PartnerAgency.DoJ);

        var result = await svc.GetForPersonAsync("p1", partnerScope);

        Assert.Equal(new[] { "r-shared" }, result.Select(r => r.RelationId).ToArray());
    }

    // ---- CreateAsync (no permission guard) ----

    [Fact]
    public async Task CreateAsync_PersistsRelation_TrimsNote_AndRecalculatesBothScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "A"));
            db.People.Add(Seed.Person("p2", "B"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.CreateAsync("p1", "p2", RelationType.Enemy, "  hostile  ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonRelations.SingleAsync(r => r.PersonAId == "p1" && r.PersonBId == "p2");
        Assert.Equal(RelationType.Enemy, stored.Type);
        Assert.Equal("hostile", stored.Note);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
        await threat.Received(1).NewCalculatePersonScoreAsync("p2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTargetEqualsSource()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("p1", "p1", RelationType.Known, null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTargetBlank()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("p1", "  ", RelationType.Known, null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenTargetNotFound()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "A"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("p1", "missing", RelationType.Known, null, Leader()));
    }

    [Fact]
    public async Task CreateAsync_Throws_OnDuplicateSameType_EvenInReverseDirection()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "A"));
            db.People.Add(Seed.Person("p2", "B"));
            // existing relation stored in the reverse (p2 -> p1) direction.
            db.PersonRelations.Add(NewRelation("r-existing", "p2", "p1", RelationType.Ally, At(1)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("p1", "p2", RelationType.Ally, null, Leader()));
    }

    // ---- UpdateAsync (RequireWriteAccess guard) ----

    [Fact]
    public async Task UpdateAsync_UpdatesTypeAndNote_AndRecalculatesOnTypeChange()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonRelations.Add(NewRelation("r1", "p1", "p2", RelationType.Known, At(1), "old"));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.UpdateAsync("r1", RelationType.Enemy, "  new  ", Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonRelations.SingleAsync(r => r.Id == "r1");
        Assert.Equal(RelationType.Enemy, stored.Type);
        Assert.Equal("new", stored.Note);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
        await threat.Received(1).NewCalculatePersonScoreAsync("p2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("missing", RelationType.Known, null, Leader()));
    }

    [Fact]
    public async Task UpdateAsync_Throws_WhenTypeChangeCollidesWithExistingRelation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonRelations.Add(NewRelation("r1", "p1", "p2", RelationType.Family, At(1)));
            db.PersonRelations.Add(NewRelation("r2", "p1", "p2", RelationType.Enemy, At(2)));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // changing r1 to Enemy duplicates r2 (same pair, same type).
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.UpdateAsync("r1", RelationType.Enemy, null, Leader()));
    }

    [Fact]
    public async Task UpdateAsync_Throws_ForOnlyReader()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.UpdateAsync("any", RelationType.Known, null, OnlyReader()));
    }

    // ---- RemoveAsync (no permission guard) ----

    [Fact]
    public async Task RemoveAsync_RemovesRelation_AndRecalculatesBothScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonRelations.Add(NewRelation("r1", "p1", "p2", RelationType.Ally, At(1)));
            db.SaveChanges();
        }
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.RemoveAsync("r1", Leader());

        // interceptor absent in tests => hard delete; row gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.PersonRelations.AnyAsync(r => r.Id == "r1"));
        await threat.Received(1).NewCalculatePersonScoreAsync("p1", Arg.Any<CancellationToken>());
        await threat.Received(1).NewCalculatePersonScoreAsync("p2", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var threat = Substitute.For<IThreatScoreService>();
        var svc = NewService(ctx, threat);

        await svc.RemoveAsync("missing", Leader());

        await threat.DidNotReceive().NewCalculatePersonScoreAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
