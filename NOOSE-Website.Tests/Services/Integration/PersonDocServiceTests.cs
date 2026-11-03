using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PersonDocService"/> over in-memory SQLite.</summary>
public sealed class PersonDocServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // Rank >= SpecialAgentInCharge(4) or admin => leadership => MayClassifiedRead.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin, no classified read.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.Trainee).Build();

    private static ViewerScope LeaderScope()
        => new(MayClassifiedRead: true, MayAllTaskforces: true, MeId: "lead", PartnerAgency: null, IsLeadership: true);

    private static ViewerScope AgentScope()
        => new(MayClassifiedRead: false, MayAllTaskforces: false, MeId: "me", PartnerAgency: null);

    private static (PersonDocService Svc, IPersonService Person, IThreatScoreService Threat) Build(SqliteTestContext ctx)
    {
        var person = Substitute.For<IPersonService>();
        var threat = Substitute.For<IThreatScoreService>();
        var svc = new PersonDocService(ctx.Factory, person, threat, Substitute.For<INotificationService>());
        return (svc, person, threat);
    }

    private static PersonDoc Doc(string personId, DateTime timestamp, Action<PersonDoc>? configure = null)
    {
        var d = new PersonDoc
        {
            PersonId = personId,
            Timestamp = timestamp,
            Outcome = MeasureOutcome.RunningStill,
        };
        configure?.Invoke(d);
        return d;
    }

    // ---------- GetForPersonAsync ----------

    [Fact]
    public async Task GetForPersonAsync_ReturnsDocs_OrderedByTimestampDescending()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(Doc("p1", T0));
            db.PersonDocs.Add(Doc("p1", T0.AddHours(2)));
            db.PersonDocs.Add(Doc("p1", T0.AddHours(1)));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetForPersonAsync("p1", AgentScope());

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { T0.AddHours(2), T0.AddHours(1), T0 }, result.Select(r => r.Doc.Timestamp).ToArray());
    }

    [Fact]
    public async Task GetForPersonAsync_ReturnsEmpty_WhenClassifiedPersonNotVisibleToViewer()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonDocs.Add(Doc("p1", T0));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        // non-leadership agent may not see the classified parent record
        var result = await svc.GetForPersonAsync("p1", AgentScope());

        Assert.Empty(result);
    }

    // ---------- GetForOrgAsync ----------

    [Fact]
    public async Task GetForOrgAsync_ReturnsDocs_WithResolvedOrgLink()
    {
        using var ctx = new SqliteTestContext();
        var faction = Seed.Faction("f1", "Ballas");
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(faction);
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(Doc("p1", T0, d => { d.OrgType = nameof(Faction); d.OrgId = "f1"; }));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetForOrgAsync(nameof(Faction), "f1", LeaderScope());

        var display = Assert.Single(result);
        Assert.Equal("Ballas", display.OrgName);
        Assert.Equal("/fraktionen/f1", display.OrgRoute);
        Assert.Equal(faction.CaseNumber, display.OrgCaseNumber);
    }

    [Fact]
    public async Task GetForOrgAsync_ReturnsEmpty_WhenOrgNotVisibleToViewer()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.SecrecyLevel = DocumentClassification.Leadership));
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(Doc("p1", T0, d => { d.OrgType = nameof(Faction); d.OrgId = "f1"; }));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        // classified faction is not visible to a plain agent
        var result = await svc.GetForOrgAsync(nameof(Faction), "f1", AgentScope());

        Assert.Empty(result);
    }

    // ---------- GetAllAsync ----------

    [Fact]
    public async Task GetAllAsync_ReturnsAllDocs_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonDocs.Add(Doc("p1", T0));
            db.PersonDocs.Add(Doc("p2", T0));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetAllAsync(isLeadership: true);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAllAsync_ExcludesClassifiedPersonDocs_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonDocs.Add(Doc("p1", T0));
            db.PersonDocs.Add(Doc("p2", T0));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        var result = await svc.GetAllAsync(isLeadership: false);

        var display = Assert.Single(result);
        Assert.Equal("p1", display.Doc.PersonId);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsDoc_AndRecomputesScores()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, threat) = Build(ctx);
        var input = new PersonDocInput
        {
            Timestamp = T0,
            Reason = "  Verhoer  ",
            Outcome = MeasureOutcome.RunningStill,
        };

        var doc = await svc.CreateAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonDocs.SingleAsync(d => d.Id == doc.Id);
        Assert.Equal("p1", stored.PersonId);
        Assert.Equal("Verhoer", stored.Reason);
        await threat.Received(1).NewCalculateForPersonAsync("p1");
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task CreateAsync_ShotOutcome_SetsDeathWindow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0, Outcome = MeasureOutcome.Shot };

        await svc.CreateAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var person = await check.People.SingleAsync(p => p.Id == "p1");
        Assert.Equal(LifeStatus.Dead, person.LifeStatus);
        Assert.Equal(T0.AddMinutes(20), person.DeadUntil);
    }

    [Fact]
    public async Task CreateAsync_InjectionOutcome_MarksMemoryDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0, Outcome = MeasureOutcome.Injection };

        var doc = await svc.CreateAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonDocs.SingleAsync(d => d.Id == doc.Id);
        Assert.True(stored.MemoryDeleted);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenActorMayNotSeeClassifiedPerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0 };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("p1", input, Junior()));

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.PersonDocs.CountAsync());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenPersonNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("missing", input, Leader()));
    }

    // ---------- CreateForNewPersonAsync ----------

    [Fact]
    public async Task CreateForNewPersonAsync_CreatesPersonThenDoc()
    {
        using var ctx = new SqliteTestContext();
        // the person service (substituted) is expected to have committed the record; mirror that in the DB for the FK
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("new-1", "Neuzugang"));
            db.SaveChanges();
        }
        var (svc, person, threat) = Build(ctx);
        person.CreateAsync(Arg.Any<PersonInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(Seed.Person("new-1", "Neuzugang"));
        var input = new PersonDocInput { Timestamp = T0, Outcome = MeasureOutcome.RunningStill };

        var doc = await svc.CreateForNewPersonAsync("  Neuzugang  ", input, Leader());

        Assert.Equal("new-1", doc.PersonId);
        await person.Received(1).CreateAsync(
            Arg.Is<PersonInput>(i => i.Name == "Neuzugang"), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
        using var check = ctx.NewContext();
        Assert.True(await check.PersonDocs.AnyAsync(d => d.Id == doc.Id && d.PersonId == "new-1"));
        await threat.Received(1).NewCalculateForPersonAsync("new-1");
    }

    [Fact]
    public async Task CreateForNewPersonAsync_Throws_OnBlankName()
    {
        using var ctx = new SqliteTestContext();
        var (svc, person, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateForNewPersonAsync("   ", input, Leader()));

        // guard fires before the person record is created
        await person.DidNotReceive().CreateAsync(
            Arg.Any<PersonInput>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndRecomputesScores()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0, d => d.Reason = "old");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, threat) = Build(ctx);
        var input = new PersonDocInput
        {
            Timestamp = T0.AddHours(3),
            Reason = "  neu  ",
            ReceivedInformation = "  info  ",
            TruthSerum = true,
            Outcome = MeasureOutcome.OfficiallyReleased,
        };

        await svc.RefreshAsync(doc.Id, input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.PersonDocs.SingleAsync(d => d.Id == doc.Id);
        Assert.Equal(T0.AddHours(3), stored.Timestamp);
        Assert.Equal("neu", stored.Reason);
        Assert.Equal("info", stored.ReceivedInformation);
        Assert.True(stored.TruthSerum);
        Assert.Equal(MeasureOutcome.OfficiallyReleased, stored.Outcome);
        await threat.Received(1).NewCalculateForPersonAsync("p1");
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task RefreshAsync_RevivesPerson_WhenOwnedShotWindowClearedByNonShotOutcome()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0, d => d.Outcome = MeasureOutcome.Shot);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p =>
            {
                p.LifeStatus = LifeStatus.Dead;
                p.DeadUntil = LifeStatusLogic.DeadUntilFrom(T0);
            }));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        // same measure time, outcome no longer "shot" => this doc's death window must be undone
        var input = new PersonDocInput { Timestamp = T0, Outcome = MeasureOutcome.OfficiallyReleased };

        await svc.RefreshAsync(doc.Id, input, Leader());

        using var check = ctx.NewContext();
        var person = await check.People.SingleAsync(p => p.Id == "p1");
        Assert.Equal(LifeStatus.Alive, person.LifeStatus);
        Assert.Null(person.DeadUntil);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenActorMayNotSeeClassifiedPerson()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0 };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync(doc.Id, input, Junior()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownDoc()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var input = new PersonDocInput { Timestamp = T0 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", input, Leader()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesDoc_AndRecomputesScores()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, threat) = Build(ctx);

        await svc.DeleteAsync(doc.Id, Leader());

        // no soft-delete interceptor in tests => the row is hard-deleted
        using var check = ctx.NewContext();
        Assert.False(await check.PersonDocs.AnyAsync(d => d.Id == doc.Id));
        await threat.Received(1).NewCalculateForPersonAsync("p1");
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task DeleteAsync_RevivesPerson_WhenDeletingOwnedShotDoc()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0, d => d.Outcome = MeasureOutcome.Shot);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p =>
            {
                p.LifeStatus = LifeStatus.Dead;
                p.DeadUntil = LifeStatusLogic.DeadUntilFrom(T0);
            }));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        await svc.DeleteAsync(doc.Id, Leader());

        using var check = ctx.NewContext();
        var person = await check.People.SingleAsync(p => p.Id == "p1");
        Assert.Equal(LifeStatus.Alive, person.LifeStatus);
        Assert.Null(person.DeadUntil);
        Assert.False(await check.PersonDocs.AnyAsync(d => d.Id == doc.Id));
    }

    [Fact]
    public async Task DeleteAsync_NoOp_OnUnknownDoc()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, threat) = Build(ctx);

        // returns without throwing and without recomputing scores
        await svc.DeleteAsync("missing", Leader());

        await threat.DidNotReceive().NewCalculateForPersonAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenActorMayNotSeeClassifiedPerson()
    {
        using var ctx = new SqliteTestContext();
        var doc = Doc("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.PersonDocs.Add(doc);
            db.SaveChanges();
        }
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(doc.Id, Junior()));

        using var check = ctx.NewContext();
        Assert.True(await check.PersonDocs.AnyAsync(d => d.Id == doc.Id));
    }
}
