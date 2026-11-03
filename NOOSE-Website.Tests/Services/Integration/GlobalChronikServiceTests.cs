using System.Security.Claims;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Timeline;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="GlobalChronikService"/> against in-memory SQLite.</summary>
public sealed class GlobalChronikServiceTests
{
    private static readonly DateTime Now = new(2026, 8, 1, 12, 0, 0, DateTimeKind.Utc);

    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("low").WithRank(Rank.Trainee).Build();
    private static ClaimsPrincipal Partner() =>
        ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

    private static ChronikQuery Window(string? type = null, string? agent = null)
        => new(Now.AddDays(-30), Now.AddDays(1),
            type is null ? null : new[] { type },
            agent is null ? null : new[] { agent });

    private static void Audit(NOOSE_Website.Data.AppDbContext db, string type, string id, AuditAction action,
        string agent = "a1", DateTime? at = null, string? changesJson = null)
        => db.AuditLogs.Add(new AuditLog
        {
            EntityType = type, EntityId = id, Action = action, Timestamp = at ?? Now,
            AgentId = agent, AgentName = agent, ChangesJson = changesJson,
        });

    [Fact]
    public async Task GetEventsAsync_ReturnsLifecycleAndClassification_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase,
                Timestamp = Now, AgentId = "a1", AgentName = "a1",
            });
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var result = await svc.GetEventsAsync(Window(), Leader());

        Assert.Equal(2, result.Events.Count);
        Assert.All(result.Events, e => Assert.Equal("Max", e.Name));
        Assert.Contains(result.Events, e => e.Category == TimelineCategory.Classification);
    }

    [Fact]
    public async Task GetEventsAsync_HidesClassifiedRecord_FromNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "s1", name: "Geheim", configure: p => p.IsClassified = true));
            Audit(db, nameof(Person), "s1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Single((await svc.GetEventsAsync(Window(), Leader())).Events);
        Assert.Empty((await svc.GetEventsAsync(Window(), Junior())).Events);
    }

    [Fact]
    public async Task GetEventsAsync_ShowsDeletionEvent_ForSoftDeletedRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "d1", name: "Weg", configure: p =>
            {
                p.IsDeleted = true;
                p.DeletedAt = Now;
            }));
            Audit(db, nameof(Person), "d1", AuditAction.Deleted);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var events = (await svc.GetEventsAsync(Window(), Leader())).Events;
        var e = Assert.Single(events);
        Assert.Equal("Weg", e.Name);
        Assert.Equal(TimelineCategory.Deletion, e.Category);
    }

    [Fact]
    public async Task GetEventsAsync_Empty_ForPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Empty((await svc.GetEventsAsync(Window(), Partner())).Events);
    }

    [Fact]
    public async Task GetEventsAsync_FiltersByType()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            Audit(db, nameof(NOOSE_Website.Data.Entities.Factions.Faction), "f1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var events = (await svc.GetEventsAsync(Window(type: nameof(NOOSE_Website.Data.Entities.Factions.Faction)), Leader())).Events;
        var e = Assert.Single(events);
        Assert.Equal("Ballas", e.Name);
    }

    [Fact]
    public async Task GetFilterOptionsAsync_ReturnsActingAgents()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Falke"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created, agent: "a1");
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var options = await svc.GetFilterOptionsAsync(Leader());

        Assert.Contains("Person", options.Types);
        Assert.Contains(options.Agents, a => a.Id == "a1");
    }

    [Fact]
    public async Task GetFilterOptionsAsync_ListsWholeRoster_EvenWithoutAuditRows()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Falke"));
            db.Users.Add(Seed.Agent("quiet", configure: a => a.Codename = "Bussard"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created, agent: "a1");
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var options = await svc.GetFilterOptionsAsync(Leader());

        Assert.Equal(new[] { "Bussard", "Falke" }, options.Agents.Select(a => a.Name).ToArray());
    }

    [Fact]
    public async Task GetFilterOptionsAsync_SkipsBlankNamesAndReadOnlySupervisors()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Falke"));
            db.Users.Add(Seed.Agent("supervisor", configure: a =>
            {
                a.Codename = "Aufsicht";
                a.IsTeamLead = true;
            }));
            db.Users.Add(Seed.Agent("applicant", status: AgentStatus.Applicant,
                configure: a => a.Codename = string.Empty));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created, agent: "a1");
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var options = await svc.GetFilterOptionsAsync(Leader());

        Assert.DoesNotContain(options.Agents, a => string.IsNullOrWhiteSpace(a.Name));
        Assert.DoesNotContain(options.Agents, a => a.Id is "supervisor" or "applicant");
    }

    [Fact]
    public async Task GetFilterOptionsAsync_UsesCurrentCodename_NotTheLoggedOne()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Neuer Name"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created, agent: "a1");
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var options = await svc.GetFilterOptionsAsync(Leader());

        var option = Assert.Single(options.Agents);
        Assert.Equal("Neuer Name", option.Name);
    }

    // ===================== child fan-out =====================

    [Fact]
    public async Task GetEventsAsync_FansInChildEvents_AnchoredOnTheirRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "p1", Text = "Sichtung am Hafen" });
            db.PersonDocs.Add(new PersonDoc { Id = "d1", PersonId = "p1", Outcome = MeasureOutcome.Shot, Timestamp = Now });
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            Audit(db, nameof(PersonDoc), "d1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var events = (await svc.GetEventsAsync(Window(), Leader())).Events;

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.Equal("Max", e.Name));
        Assert.All(events, e => Assert.Equal("/personen/p1", e.Href));

        var comment = Assert.Single(events, e => e.Category == TimelineCategory.Comment);
        Assert.Equal("Sichtung am Hafen", comment.Detail);
        var doc = Assert.Single(events, e => e.Category == TimelineCategory.Doc);
        Assert.Contains("Ausgang", doc.Detail);
    }

    [Fact]
    public async Task GetEventsAsync_HidesChildEvent_WhenItsRecordIsClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "s1", name: "Geheim", configure: p => p.IsClassified = true));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "s1", Text = "streng vertraulich" });
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Single((await svc.GetEventsAsync(Window(), Leader())).Events);
        Assert.Empty((await svc.GetEventsAsync(Window(), Junior())).Events);
    }

    [Fact]
    public async Task GetEventsAsync_HidesChildEvent_OnTruOnlyRecord()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "t1", name: "TRU-Fall", configure: p =>
            {
                p.IsClassified = true;
                p.IsTRUClassified = true;
            }));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "t1", Text = "nur TRU" });
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var tru = ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().Build();

        Assert.Empty((await svc.GetEventsAsync(Window(), Junior())).Events);
        Assert.Single((await svc.GetEventsAsync(Window(), tru)).Events);
    }

    [Fact]
    public async Task GetEventsAsync_TypeFilter_AlsoBitesPolymorphicChildren()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "p1", Text = "an der Person" });
            db.Comments.Add(new Comment { Id = "k2", EntityType = nameof(Faction), EntityId = "f1", Text = "an der Fraktion" });
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            Audit(db, nameof(Comment), "k2", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var events = (await svc.GetEventsAsync(Window(type: nameof(Faction)), Leader())).Events;

        var e = Assert.Single(events);
        Assert.Equal("Ballas", e.Name);
    }

    [Fact]
    public async Task GetEventsAsync_MembershipEvent_NamesTheMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.People.Add(Seed.Person(id: "p1", name: "Marco"));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1", Rank = "Soldat" });
            Audit(db, nameof(FactionMember), "m1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var e = Assert.Single((await svc.GetEventsAsync(Window(), Leader())).Events);

        Assert.Equal("Ballas", e.Name);
        Assert.Equal(TimelineCategory.Membership, e.Category);
        Assert.Equal("Marco · Soldat", e.Detail);
    }

    [Fact]
    public async Task GetEventsAsync_MembershipEvent_MasksAClassifiedMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.People.Add(Seed.Person(id: "p1", name: "Verdeckter", configure: p => p.IsClassified = true));
            db.FactionMembers.Add(new FactionMember { Id = "m1", FactionId = "f1", PersonId = "p1" });
            Audit(db, nameof(FactionMember), "m1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var e = Assert.Single((await svc.GetEventsAsync(Window(), Junior())).Events);

        Assert.Equal("Ballas", e.Name);
        Assert.DoesNotContain("Verdeckter", e.Detail);
    }

    [Fact]
    public async Task GetEventsAsync_DropsAutomaticLinks()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Links.Add(new Link { Id = "v1", SourceType = nameof(Person), SourceId = "p1", TargetType = nameof(Faction), TargetId = "f1", Automatic = true });
            Audit(db, nameof(Link), "v1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Empty((await svc.GetEventsAsync(Window(), Leader())).Events);
    }

    // ===================== field diffs =====================

    [Fact]
    public async Task GetEventsAsync_CarriesFieldChanges_AndHidesMetaFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified,
                changesJson: """{"Name":["Max","Maximilian"],"GeaendertAm":["2026-01-01","2026-08-01"]}""");
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var e = Assert.Single((await svc.GetEventsAsync(Window(), Leader())).Events);

        var change = Assert.Single(e.Changes!);
        Assert.Equal("Name", change.Field);
        Assert.Equal("Max", change.Alt);
        Assert.Equal("Maximilian", change.New);
    }

    // ===================== paging =====================

    [Fact]
    public async Task GetEventsAsync_PagesWholeDays_WithoutOverlapOrGaps()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now);
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now.AddDays(-1));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now.AddDays(-2));
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);
        var query = Window() with { MinEvents = 1 };

        var first = await svc.GetEventsAsync(query, Leader());
        Assert.Single(first.Events);
        Assert.True(first.HasMore);
        Assert.NotNull(first.NextCursorUtc);

        var second = await svc.GetEventsAsync(query with { BeforeUtc = first.NextCursorUtc }, Leader());
        Assert.Single(second.Events);
        Assert.True(second.Events[0].Timestamp < first.Events[0].Timestamp);

        var third = await svc.GetEventsAsync(query with { BeforeUtc = second.NextCursorUtc }, Leader());
        Assert.Single(third.Events);

        var stamps = first.Events.Concat(second.Events).Concat(third.Events)
            .Select(e => e.Timestamp).ToList();
        Assert.Equal(3, stamps.Distinct().Count());
    }

    [Fact]
    public async Task GetEventsAsync_ReportsNoMore_WhenWindowIsExhausted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var result = await svc.GetEventsAsync(Window(), Leader());

        Assert.Single(result.Events);
        Assert.False(result.HasMore);
        Assert.Null(result.NextCursorUtc);
    }

    // ===================== threat score =====================

    [Fact]
    public async Task GetEventsAsync_EmitsScoreJump_OnlyAboveThreshold()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.Factions.Add(Seed.Faction(id: "f2", name: "Vagos"));
            Score(db, "f1", 30, Now.AddDays(-3));
            Score(db, "f1", 61, Now.AddDays(-1));   // +31 → jump
            Score(db, "f2", 40, Now.AddDays(-3));
            Score(db, "f2", 44, Now.AddDays(-1));   // +4 → no jump
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var events = (await svc.GetEventsAsync(Window(), Leader())).Events;

        var e = Assert.Single(events, x => x.Category == TimelineCategory.ThreatScore);
        Assert.Equal("Ballas", e.Name);
        Assert.Contains("30 → 61", e.Title);
    }

    [Fact]
    public async Task GetEventsAsync_FindsScoreOnlyDays_WhenOnlyScoreCategoryIsSelected()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            // audit noise on the newest days must not crowd out the older score-only day
            for (var day = 0; day < 6; day++)
            {
                Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now.AddDays(-day));
            }
            Score(db, "f1", 20, Now.AddDays(-11));
            Score(db, "f1", 70, Now.AddDays(-10));
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var query = Window() with { Categories = new[] { TimelineCategory.ThreatScore } };
        var e = Assert.Single((await svc.GetEventsAsync(query, Leader())).Events);

        Assert.Equal(TimelineCategory.ThreatScore, e.Category);
        Assert.Contains("20 → 70", e.Title);
    }

    [Fact]
    public async Task GetEventsAsync_SuppressesScoreJumps_WhenFilteredByAgent()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction(id: "f1", name: "Ballas"));
            Score(db, "f1", 30, Now.AddDays(-3));
            Score(db, "f1", 61, Now.AddDays(-1));
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Empty((await svc.GetEventsAsync(Window(agent: "a1"), Leader())).Events);
    }

    // ===================== post filters =====================

    [Fact]
    public async Task GetEventsAsync_FiltersByCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "p1", Text = "Notiz" });
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var query = Window() with { Categories = new[] { TimelineCategory.Comment } };
        var e = Assert.Single((await svc.GetEventsAsync(query, Leader())).Events);

        Assert.Equal(TimelineCategory.Comment, e.Category);
    }

    [Fact]
    public async Task GetEventsAsync_FiltersByFreeText_AcrossNameAndDetail()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            db.People.Add(Seed.Person(id: "p2", name: "Erika"));
            db.Comments.Add(new Comment { Id = "k1", EntityType = nameof(Person), EntityId = "p2", Text = "Hafen beobachtet" });
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            Audit(db, nameof(Comment), "k1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var byName = (await svc.GetEventsAsync(Window() with { Text = "max" }, Leader())).Events;
        Assert.Equal("Max", Assert.Single(byName).Name);

        var byDetail = (await svc.GetEventsAsync(Window() with { Text = "hafen" }, Leader())).Events;
        Assert.Equal("Erika", Assert.Single(byDetail).Name);
    }

    // ===================== density =====================

    [Fact]
    public async Task GetDensityAsync_CountsEveryAuditRow_AndGroupsByLocalDay()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now);
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now.AddHours(-1));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now.AddDays(-2));
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var density = await svc.GetDensityAsync(Window(), Leader());

        Assert.Equal(3, density.Total);
        Assert.Equal(ChronikBucketUnit.Day, density.Unit);
        Assert.Equal(1, density.DistinctRecords);
        Assert.Equal(1, density.DistinctAgents);
        // the two rows an hour apart share a local day, the third sits on its own
        Assert.Equal(2, density.Buckets.Max(b => b.Total));
        Assert.Equal(2, density.Buckets.Count(b => b.Total > 0));
        Assert.False(density.Capped);
    }

    [Fact]
    public async Task GetDensityAsync_FillsQuietBucketsWithZero()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var density = await svc.GetDensityAsync(Window(), Leader());

        // a 31-day window is a contiguous run of daily buckets, not just the days that carry events
        Assert.True(density.Buckets.Count > 25);
        Assert.Contains(density.Buckets, b => b.Total == 0);
        for (var i = 1; i < density.Buckets.Count; i++)
        {
            Assert.Equal(
                density.Buckets[i - 1].StartLocal.AddDays(1),
                density.Buckets[i].StartLocal);
        }
    }

    [Fact]
    public async Task GetDensityAsync_SplitsBucketsIntoBandGroups()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now);
            Audit(db, nameof(Person), "p1", AuditAction.Created, at: Now);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        var density = await svc.GetDensityAsync(Window(), Leader());
        var bucket = Assert.Single(density.Buckets.Where(b => b.Total > 0));

        // Modified maps to Change (Bearbeitung), Created to Asset (Inhalte)
        Assert.Equal(2, bucket.Segments.Count);
        Assert.Contains(bucket.Segments, s => s.Slot == ActivityBandDisplay.Slot(TimelineCategory.Change));
        Assert.Contains(bucket.Segments, s => s.Slot == ActivityBandDisplay.Slot(TimelineCategory.Asset));
        Assert.Equal(bucket.Total, bucket.Segments.Sum(s => s.Count));
    }

    [Fact]
    public async Task GetDensityAsync_SkipsRecordsTheViewerMayNotSee()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "s1", name: "Geheim", configure: p => p.IsClassified = true));
            Audit(db, nameof(Person), "s1", AuditAction.Modified, at: Now);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        // the band used to count raw audit rows, so a bar could exceed the feed below it
        Assert.Equal(1, (await svc.GetDensityAsync(Window(), Leader())).Total);
        Assert.Equal(0, (await svc.GetDensityAsync(Window(), Junior())).Total);
    }

    [Fact]
    public async Task GetDensityAsync_HonoursTheCategoryFilter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Modified, at: Now);
            db.ClassificationHistory.Add(new ClassificationHistory
            {
                EntityType = nameof(Person), EntityId = "p1", Value = Classification.SuspicionCase,
                Timestamp = Now, AgentId = "a1", AgentName = "a1",
            });
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Equal(2, (await svc.GetDensityAsync(Window(), Leader())).Total);

        var onlyClassification = Window() with { Categories = new[] { TimelineCategory.Classification } };
        Assert.Equal(1, (await svc.GetDensityAsync(onlyClassification, Leader())).Total);
    }

    [Theory]
    [InlineData(1, ChronikBucketUnit.Hour)]
    [InlineData(30, ChronikBucketUnit.Day)]
    [InlineData(120, ChronikBucketUnit.Week)]
    [InlineData(500, ChronikBucketUnit.Month)]
    public async Task GetDensityAsync_DerivesTheBucketUnitFromTheWindow(int days, ChronikBucketUnit expected)
    {
        using var ctx = new SqliteTestContext();
        var svc = new GlobalChronikService(ctx.Factory);

        var density = await svc.GetDensityAsync(new ChronikQuery(Now.AddDays(-days), Now), Leader());

        Assert.Equal(expected, density.Unit);
    }

    [Fact]
    public async Task GetDensityAsync_SurvivesAReversedWindow()
    {
        using var ctx = new SqliteTestContext();
        var svc = new GlobalChronikService(ctx.Factory);

        // ?von=/?bis= are not order-checked, so the service must not divide by a negative day count
        var density = await svc.GetDensityAsync(new ChronikQuery(Now, Now.AddDays(-10)), Leader());

        Assert.True(density.WindowDays >= 1);
        Assert.True(density.AveragePerDay >= 0);
    }

    [Fact]
    public async Task GetDensityAsync_Empty_ForPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(id: "p1", name: "Max"));
            Audit(db, nameof(Person), "p1", AuditAction.Created);
            db.SaveChanges();
        }
        var svc = new GlobalChronikService(ctx.Factory);

        Assert.Empty((await svc.GetDensityAsync(Window(), Partner())).Buckets);
    }

    private static void Score(NOOSE_Website.Data.AppDbContext db, string factionId, int score, DateTime at)
        => db.ThreatScoreHistory.Add(new ThreatScoreHistory
        {
            EntityType = nameof(Faction), EntityId = factionId, Score = score, Timestamp = at,
        });
}
