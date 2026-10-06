using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.People;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="ObservationService"/> over in-memory SQLite.</summary>
public sealed class ObservationServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    // Rank >= SupervisorySpecialAgent(4) => leadership => MayClassifiedRead.
    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin, no classified read.
    private static ClaimsPrincipal Junior()
        => ClaimsPrincipalBuilder.Agent("junior").WithRank(Rank.JuniorAgent).Build();

    private static ViewerScope LeaderScope()
        => new(MayClassifiedRead: true, MayAllTaskforces: true, MeId: "lead", PartnerAgency: null, IsLeadership: true);

    private static ViewerScope AgentScope()
        => new(MayClassifiedRead: false, MayAllTaskforces: false, MeId: "me", PartnerAgency: null);

    private static ViewerScope PartnerScope()
        => new(MayClassifiedRead: false, MayAllTaskforces: false, MeId: "partner-1", PartnerAgency: PartnerAgency.DoJ);

    private static (ObservationService Svc, IThreatScoreService Threat) Build(
        SqliteTestContext ctx, INotificationService? notifications = null)
    {
        var threat = Substitute.For<IThreatScoreService>();
        var svc = new ObservationService(ctx.Factory, threat, notifications ?? Substitute.For<INotificationService>());
        return (svc, threat);
    }

    private static Observation Obs(string personId, DateTime start, Action<Observation>? configure = null)
    {
        var o = new Observation { PersonId = personId, Start = start };
        configure?.Invoke(o);
        return o;
    }

    // ---------- GetForPersonAsync ----------

    [Fact]
    public async Task GetForPersonAsync_ReturnsObservations_OrderedByStartDescending_WithResolvedAgentAndOrg()
    {
        using var ctx = new SqliteTestContext();
        var faction = Seed.Faction("f1", "Ballas");
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("obs-agent"));
            db.Factions.Add(faction);
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(Obs("p1", T0));
            db.Observations.Add(Obs("p1", T0.AddHours(2), o =>
            {
                o.ObservingAgentId = "obs-agent";
                o.OrgType = nameof(Faction);
                o.OrgId = "f1";
            }));
            db.Observations.Add(Obs("p1", T0.AddHours(1)));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForPersonAsync("p1", LeaderScope());

        Assert.Equal(3, result.Count);
        Assert.Equal(new[] { T0.AddHours(2), T0.AddHours(1), T0 }, result.Select(r => r.Obs.Start).ToArray());
        // top entry carries the resolved agent codename and org display
        var top = result[0];
        Assert.Equal("Codename-obs-agent", top.AgentCodename);
        Assert.Equal("Ballas", top.OrgName);
        Assert.Equal(faction.CaseNumber, top.OrgCaseNumber);
        Assert.Equal("/fraktionen/f1", top.OrgRoute);
    }

    [Fact]
    public async Task GetForPersonAsync_ReturnsEmpty_WhenClassifiedPersonNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.Observations.Add(Obs("p1", T0));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // non-leadership agent may not see the classified parent record
        var result = await svc.GetForPersonAsync("p1", AgentScope());

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetForPersonAsync_Partner_ReturnsChildren_WhenParentSharedWhole()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(Obs("p1", T0));
            // person released whole to DoJ => children (observations) visible
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = nameof(Person),
                EntityId = "p1",
                Agency = PartnerAgency.DoJ,
                PartnerAgentId = null,
                IncludesChildren = true,
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForPersonAsync("p1", PartnerScope());

        Assert.Single(result);
    }

    // ---------- GetAllAsync ----------

    [Fact]
    public async Task GetAllAsync_ReturnsAll_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.Observations.Add(Obs("p1", T0));
            db.Observations.Add(Obs("p2", T0));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetAllAsync(isLeadership: true);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAllAsync_ExcludesClassifiedPersonObservations_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.People.Add(Seed.Person("p2", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.Observations.Add(Obs("p1", T0));
            db.Observations.Add(Obs("p2", T0));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetAllAsync(isLeadership: false);

        var display = Assert.Single(result);
        Assert.Equal("p1", display.Obs.PersonId);
    }

    [Fact]
    public async Task GetAllAsync_ExcludesObservations_WhenPersonSoftDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // soft-deleted person is hidden by the global query filter => navigation resolves null => excluded
            db.People.Add(Seed.Person("p1", configure: p => p.IsDeleted = true));
            db.Observations.Add(Obs("p1", T0));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetAllAsync(isLeadership: true);

        Assert.Empty(result);
    }

    // ---------- GetForOrgAsync ----------

    [Fact]
    public async Task GetForOrgAsync_ReturnsObservations_WithResolvedOrgLink()
    {
        using var ctx = new SqliteTestContext();
        var faction = Seed.Faction("f1", "Ballas");
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(faction);
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(Obs("p1", T0, o => { o.OrgType = nameof(Faction); o.OrgId = "f1"; }));
            // linked to a different org => filtered out
            db.Observations.Add(Obs("p1", T0.AddHours(1), o => { o.OrgType = nameof(Faction); o.OrgId = "other"; }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetForOrgAsync(nameof(Faction), "f1", isLeadership: true);

        var display = Assert.Single(result);
        Assert.Equal("Ballas", display.OrgName);
        Assert.Equal("/fraktionen/f1", display.OrgRoute);
        Assert.Equal(faction.CaseNumber, display.OrgCaseNumber);
    }

    [Fact]
    public async Task GetForOrgAsync_ReturnsEmpty_WhenOrgNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Ballas", f => f.SecrecyLevel = DocumentClassification.Leadership));
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(Obs("p1", T0, o => { o.OrgType = nameof(Faction); o.OrgId = "f1"; }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // classified faction is not visible to a plain (non-leadership) viewer
        var result = await svc.GetForOrgAsync(nameof(Faction), "f1", isLeadership: false);

        Assert.Empty(result);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_NotifiesMentions_ScopedToBothTextFields_GatedOnThePerson()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p7"));
            db.SaveChanges();
        }
        var notifications = Substitute.For<INotificationService>();
        var (svc, _) = Build(ctx, notifications);
        var input = new ObservationInput { Start = T0, Sighting = "Treffen", Result = "Folgerung" };

        await svc.CreateAsync("p7", input, Leader());

        // both free-text fields are scanned as one; gate is the person, not the observation
        await notifications.Received(1).NotifyMentionedDeltaAsync(
            Arg.Is<string?>(s => s == null), Arg.Is<string?>(s => s == "Treffen Folgerung"),
            Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Is<string>(t => t == "Person"), Arg.Is<string>(i => i == "p7"),
            Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>(), "Observation");
    }

    [Fact]
    public async Task CreateAsync_PersistsObservation_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, threat) = Build(ctx);
        var input = new ObservationInput
        {
            Start = T0,
            End = T0.AddHours(1),
            Location = "  Vinewood  ",
            Sighting = "  Treffen  ",
            Result = "  nichts  ",
            OrgType = nameof(Faction),
            OrgId = "  f1  ",
        };

        var obs = await svc.CreateAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Observations.SingleAsync(o => o.Id == obs.Id);
        Assert.Equal("p1", stored.PersonId);
        Assert.Equal(T0, stored.Start);
        Assert.Equal(T0.AddHours(1), stored.End);
        Assert.Equal("Vinewood", stored.Location);
        Assert.Equal("Treffen", stored.Sighting);
        Assert.Equal("nichts", stored.Result);
        Assert.Equal("f1", stored.OrgId);
        Assert.Equal(nameof(Faction), stored.OrgType);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task CreateAsync_NullsOrgType_WhenOrgIdMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);
        // org type without an id must not persist an orphan type
        var input = new ObservationInput { Start = T0, OrgType = nameof(Faction), OrgId = "   " };

        var obs = await svc.CreateAsync("p1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Observations.SingleAsync(o => o.Id == obs.Id);
        Assert.Null(stored.OrgId);
        Assert.Null(stored.OrgType);
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenPersonNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = new ObservationInput { Start = T0 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync("missing", input, Leader()));
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
        var (svc, _) = Build(ctx);
        var input = new ObservationInput { Start = T0 };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync("p1", input, Junior()));

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.Observations.CountAsync());
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        var obs = Obs("p1", T0, o => o.Location = "old");
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(obs);
            db.SaveChanges();
        }
        var (svc, threat) = Build(ctx);
        var input = new ObservationInput
        {
            Start = T0.AddHours(3),
            End = T0.AddHours(4),
            Location = "  neu  ",
            Sighting = "  gesehen  ",
            Result = "  Ergebnis  ",
            OrgType = nameof(PersonGroup),
            OrgId = "  g1  ",
        };

        await svc.RefreshAsync(obs.Id, input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Observations.SingleAsync(o => o.Id == obs.Id);
        Assert.Equal(T0.AddHours(3), stored.Start);
        Assert.Equal(T0.AddHours(4), stored.End);
        Assert.Equal("neu", stored.Location);
        Assert.Equal("gesehen", stored.Sighting);
        Assert.Equal("Ergebnis", stored.Result);
        Assert.Equal("g1", stored.OrgId);
        Assert.Equal(nameof(PersonGroup), stored.OrgType);
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownObservation()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = new ObservationInput { Start = T0 };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", input, Leader()));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenActorMayNotSeeClassifiedPerson()
    {
        using var ctx = new SqliteTestContext();
        var obs = Obs("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.Observations.Add(obs);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);
        var input = new ObservationInput { Start = T0 };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync(obs.Id, input, Junior()));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_RemovesObservation_AndRecomputesScore()
    {
        using var ctx = new SqliteTestContext();
        var obs = Obs("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1"));
            db.Observations.Add(obs);
            db.SaveChanges();
        }
        var (svc, threat) = Build(ctx);

        await svc.DeleteAsync(obs.Id, Leader());

        // no soft-delete interceptor in tests => the row is hard-deleted
        using var check = ctx.NewContext();
        Assert.False(await check.Observations.AnyAsync(o => o.Id == obs.Id));
        await threat.Received(1).NewCalculatePersonScoreAsync("p1");
    }

    [Fact]
    public async Task DeleteAsync_NoOp_OnUnknownObservation()
    {
        using var ctx = new SqliteTestContext();
        var (svc, threat) = Build(ctx);

        // returns without throwing and without recomputing scores
        await svc.DeleteAsync("missing", Leader());

        await threat.DidNotReceive().NewCalculatePersonScoreAsync(Arg.Any<string>());
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenActorMayNotSeeClassifiedPerson()
    {
        using var ctx = new SqliteTestContext();
        var obs = Obs("p1", T0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", configure: p => p.SecrecyLevel = DocumentClassification.Leadership));
            db.Observations.Add(obs);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(obs.Id, Junior()));

        using var check = ctx.NewContext();
        Assert.True(await check.Observations.AnyAsync(o => o.Id == obs.Id));
    }
}
