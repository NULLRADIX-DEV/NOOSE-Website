using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DashboardService"/> over in-memory SQLite. All methods are read-only (no Permission guards).</summary>
public sealed class DashboardServiceTests
{
    // --- construction / collaborator setup ---

    private static (DashboardService Svc, IRequestService Requests, IRecencyService Recency) Build(
        SqliteTestContext ctx,
        IReadOnlyDictionary<string, RecencySettings>? settings = null,
        int openRequestCount = 0)
    {
        var requests = Substitute.For<IRequestService>();
        requests.GetOpenCountAsync(Arg.Any<bool>(), Arg.Any<CancellationToken>()).Returns(openRequestCount);

        var recency = Substitute.For<IRecencyService>();
        recency.GetAllSettingsAsync(Arg.Any<CancellationToken>())
               .Returns(settings ?? SettingsFor(agingDisabled: true));

        var svc = new DashboardService(ctx.Factory, requests, recency);
        return (svc, requests, recency);
    }

    // The service indexes settings[nameof(T)] for every dashboard type; supply all seven keys.
    private static IReadOnlyDictionary<string, RecencySettings> SettingsFor(
        int warningDays = 10, int staleDays = 30, bool agingDisabled = false)
    {
        var s = new RecencySettings(warningDays, staleDays, agingDisabled);
        return new Dictionary<string, RecencySettings>
        {
            [nameof(Person)] = s,
            [nameof(Faction)] = s,
            [nameof(PersonGroup)] = s,
            [nameof(Party)] = s,
            [nameof(Operation)] = s,
            [nameof(Taskforce)] = s,
            [nameof(Case)] = s,
        };
    }

    // --- entity factories (unique case numbers per id to satisfy the unique index) ---

    private static Person Per(string id, string name = "Person", Action<Person>? cfg = null)
        => Seed.Person(id, name, p => { p.CaseNumber = $"FIB-P-{id}"; cfg?.Invoke(p); });

    private static Faction Fac(string id, string name = "Faction", Action<Faction>? cfg = null)
        => Seed.Faction(id, name, f => { f.CaseNumber = $"FIB-F-{id}"; cfg?.Invoke(f); });

    private static Case Cas(string id, string title = "Case", Action<Case>? cfg = null)
        => Seed.Case(id, title, c => { c.CaseNumber = $"FIB-V-{id}"; cfg?.Invoke(c); });

    private static PersonGroup Grp(string id, string name = "Group", Action<PersonGroup>? cfg = null)
    {
        var g = new PersonGroup { Id = id, Name = name, CaseNumber = $"FIB-G-{id}", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        cfg?.Invoke(g);
        return g;
    }

    private static Party Pty(string id, string name = "Party", Action<Party>? cfg = null)
    {
        var p = new Party { Id = id, Name = name, CaseNumber = $"FIB-PT-{id}", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        cfg?.Invoke(p);
        return p;
    }

    private static Operation Op(string id, string title = "Operation", Action<Operation>? cfg = null)
    {
        var o = new Operation { Id = id, Title = title, CaseNumber = $"FIB-OP-{id}", CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        cfg?.Invoke(o);
        return o;
    }

    private static Taskforce Tf(string id, string name = "Taskforce", TaskforceStatus status = TaskforceStatus.Approved, Action<Taskforce>? cfg = null)
    {
        var t = new Taskforce { Id = id, Name = $"{name}-{id}", CaseNumber = $"FIB-TF-{id}", Status = status, CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        cfg?.Invoke(t);
        return t;
    }

    // ===================== GetMetricsAsync =====================

    [Fact]
    public async Task GetMetricsAsync_Leadership_CountsAllTiles()
    {
        using var ctx = new SqliteTestContext();
        // aging disabled everywhere so StaleRecords is decoupled from wall-clock date math.
        var (svc, _, _) = Build(ctx, SettingsFor(agingDisabled: true), openRequestCount: 3);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("p1", "Plain"));
            db.People.Add(Per("p2", "Secret", p => p.IsClassified = true));
            db.Factions.Add(Fac("f1"));
            db.PersonGroups.Add(Grp("g1"));
            db.Parties.Add(Pty("pt1"));
            db.Operations.Add(Op("o1"));
            db.Cases.Add(Cas("c1", "Open", c => c.Status = CaseStatus.Open));
            db.Cases.Add(Cas("c2", "Done", c => c.Status = CaseStatus.Completed));
            db.Users.Add(Seed.Agent("u1", status: AgentStatus.Pending));
            db.Users.Add(Seed.Agent("u2", configure: a => a.NameChangeRequestedAt = DateTime.UtcNow));
            db.Taskforces.Add(Tf("tf1", status: TaskforceStatus.Requested));
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "u1", Status = PromotionStatus.Requested });
            db.SaveChanges();
        }

        var m = await svc.GetMetricsAsync(isLeadership: true, meId: "lead");

        Assert.Equal(2, m.People);                    // both, incl. classified
        Assert.Equal(3, m.FactionsAndGroups);         // faction + group + party
        Assert.Equal(1, m.Operations);
        Assert.Equal(1, m.OpenCases);                 // completed excluded
        Assert.Equal(7, m.OpenRequests);              // 3 upgrades + pending + name-change + requested TF + promotion
        Assert.Equal(1, m.Classified);                // only the classified person
        Assert.Equal(0, m.StaleRecords);
    }

    [Fact]
    public async Task GetMetricsAsync_NonLeadership_ExcludesClassified_ClassifiedTileZero()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(agingDisabled: true), openRequestCount: 0);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("p1", "Plain"));
            db.People.Add(Per("p2", "Secret", p => p.IsClassified = true));
            db.Factions.Add(Fac("f1"));
            db.Factions.Add(Fac("f2", "SecretFaction", f => f.IsClassified = true));
            db.PersonGroups.Add(Grp("g1"));
            db.Operations.Add(Op("o1"));
            db.Operations.Add(Op("o2", "SecretOp", o => o.IsClassified = true));
            db.Cases.Add(Cas("c1", "Open", c => c.Status = CaseStatus.Open));
            db.Cases.Add(Cas("c2", "OpenSecret", c => { c.Status = CaseStatus.Open; c.IsClassified = true; }));
            // requested taskforce is invisible to a non-member non-leadership caller.
            db.Taskforces.Add(Tf("tf1", status: TaskforceStatus.Requested));
            db.SaveChanges();
        }

        var m = await svc.GetMetricsAsync(isLeadership: false, meId: "me");

        Assert.Equal(1, m.People);                 // classified hidden
        Assert.Equal(2, m.FactionsAndGroups);      // 1 faction (f2 hidden) + 1 group
        Assert.Equal(1, m.Operations);             // o2 hidden
        Assert.Equal(1, m.OpenCases);              // c2 hidden
        Assert.Equal(0, m.OpenRequests);           // requested TF not visible; nothing else pending
        Assert.Equal(0, m.Classified);             // classified tile is leadership-only
    }

    [Fact]
    public async Task GetMetricsAsync_StaleRecords_CountsPastRedThreshold_ExemptAndStateExcluded()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(warningDays: 10, staleDays: 30, agingDisabled: false));
        var old = DateTime.UtcNow.AddDays(-100);
        var fresh = DateTime.UtcNow.AddDays(-1);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("stale", "Stale", p => p.CreatedAt = old));
            db.People.Add(Per("fresh", "Fresh", p => p.CreatedAt = fresh));
            db.People.Add(Per("exempt", "Exempt", p => { p.CreatedAt = old; p.AgingDisabled = true; }));
            db.Factions.Add(Fac("fstale", "StaleFaction", f => f.CreatedAt = old));
            db.Factions.Add(Fac("fstate", "StateFaction", f => { f.CreatedAt = old; f.IsStateFaction = true; }));
            db.SaveChanges();
        }

        var m = await svc.GetMetricsAsync(isLeadership: true, meId: "lead");

        // 1 stale person + 1 stale faction; fresh, per-record exempt, and state faction all drop out.
        Assert.Equal(2, m.StaleRecords);
    }

    // ===================== GetUpdateNeedAsync =====================

    [Fact]
    public async Task GetUpdateNeedAsync_ReturnsStaleAndWarning_OldestFirst_WithLevels()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(warningDays: 10, staleDays: 30, agingDisabled: false));
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("old", "Oldest", p => p.CreatedAt = DateTime.UtcNow.AddDays(-100)));
            db.People.Add(Per("mid", "Middle", p => p.CreatedAt = DateTime.UtcNow.AddDays(-15)));
            db.People.Add(Per("new", "Recent", p => p.CreatedAt = DateTime.UtcNow.AddDays(-1)));
            db.SaveChanges();
        }

        var list = await svc.GetUpdateNeedAsync(isLeadership: false, meId: null);

        Assert.Equal(2, list.Count);                                   // recent one is still fresh
        Assert.Equal("Oldest", list[0].Name);                          // oldest first
        Assert.Equal("Middle", list[1].Name);
        Assert.Equal(RecencyLevel.Stale, list[0].Level);               // >= staleDays
        Assert.Equal(RecencyLevel.Warning, list[1].Level);             // between warning and stale
        Assert.Equal(DashboardRecordType.Person, list[0].Type);
        Assert.Equal("/personen/old", list[0].Href);
    }

    [Fact]
    public async Task GetUpdateNeedAsync_Filters_Classified_Exempt_NonLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(warningDays: 10, staleDays: 30, agingDisabled: false));
        var old = DateTime.UtcNow.AddDays(-100);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("plain", "Plain", p => p.CreatedAt = old));
            db.People.Add(Per("secret", "Secret", p => { p.CreatedAt = old; p.IsClassified = true; }));
            db.People.Add(Per("exempt", "Exempt", p => { p.CreatedAt = old; p.AgingDisabled = true; }));
            db.SaveChanges();
        }

        var list = await svc.GetUpdateNeedAsync(isLeadership: false, meId: "me");

        // only the plain, non-exempt record survives for a non-leadership caller.
        Assert.Single(list);
        Assert.Equal("Plain", list[0].Name);
    }

    [Fact]
    public async Task GetUpdateNeedAsync_AgingDisabledType_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(agingDisabled: true));
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("old", "Oldest", p => p.CreatedAt = DateTime.UtcNow.AddDays(-500)));
            db.SaveChanges();
        }

        Assert.Empty(await svc.GetUpdateNeedAsync(isLeadership: true, meId: "lead"));
    }

    [Fact]
    public async Task GetUpdateNeedAsync_RespectsMax_GlobalOldestFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, SettingsFor(warningDays: 10, staleDays: 30, agingDisabled: false));
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("older", "PersonOlder", p => p.CreatedAt = DateTime.UtcNow.AddDays(-200)));
            db.Factions.Add(Fac("newer", "FactionNewer", f => f.CreatedAt = DateTime.UtcNow.AddDays(-50)));
            db.SaveChanges();
        }

        var list = await svc.GetUpdateNeedAsync(isLeadership: true, meId: "lead", max: 1);

        Assert.Single(list);
        Assert.Equal("PersonOlder", list[0].Name);   // globally oldest wins the single slot
    }

    // ===================== GetFactionsByHazardAsync =====================

    [Fact]
    public async Task GetFactionsByHazardAsync_OrdersByScoreDesc_MapsHazard()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Fac("a", "Critical", f => f.ThreatScore = 90));
            db.Factions.Add(Fac("b", "Medium", f => f.ThreatScore = 40));
            db.Factions.Add(Fac("c", "None", f => f.ThreatScore = null));
            db.SaveChanges();
        }

        var list = await svc.GetFactionsByHazardAsync(isLeadership: false);

        Assert.Equal(new[] { "Critical", "Medium", "None" }, list.Select(f => f.Name).ToArray());
        Assert.Equal(HazardLevel.Critical, list[0].Level);
        Assert.Equal(HazardLevel.Medium, list[1].Level);
        Assert.Equal(HazardLevel.No, list[2].Level);
        Assert.Equal("/fraktionen/a", list[0].Href);
    }

    [Fact]
    public async Task GetFactionsByHazardAsync_NonLeadership_ExcludesClassified()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Fac("a", "Public", f => f.ThreatScore = 50));
            db.Factions.Add(Fac("b", "Secret", f => { f.ThreatScore = 99; f.IsClassified = true; }));
            db.SaveChanges();
        }

        var list = await svc.GetFactionsByHazardAsync(isLeadership: false);

        Assert.Single(list);
        Assert.Equal("Public", list[0].Name);
    }

    // ===================== GetPeopleByHazardAsync =====================

    [Fact]
    public async Task GetPeopleByHazardAsync_OnlyScored_OrderedDesc()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("hi", "High", p => p.ThreatScore = 80));
            db.People.Add(Per("lo", "Low", p => p.ThreatScore = 20));
            db.People.Add(Per("zero", "Zero", p => p.ThreatScore = 0));   // excluded (not > 0)
            db.People.Add(Per("null", "Null", p => p.ThreatScore = null)); // excluded (no score)
            db.SaveChanges();
        }

        var list = await svc.GetPeopleByHazardAsync(isLeadership: true);

        Assert.Equal(new[] { "High", "Low" }, list.Select(p => p.Name).ToArray());
        Assert.Equal(HazardLevel.Critical, list[0].Level);
        Assert.Equal(HazardLevel.Low, list[1].Level);
        Assert.Equal("/personen/hi", list[0].Href);
    }

    [Fact]
    public async Task GetPeopleByHazardAsync_NonLeadership_ExcludesClassified()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        using (var db = ctx.NewContext())
        {
            db.People.Add(Per("pub", "Public", p => p.ThreatScore = 60));
            db.People.Add(Per("sec", "Secret", p => { p.ThreatScore = 90; p.IsClassified = true; }));
            db.SaveChanges();
        }

        var list = await svc.GetPeopleByHazardAsync(isLeadership: false);

        Assert.Single(list);
        Assert.Equal("Public", list[0].Name);
    }

    // ===================== GetDistributionsAsync =====================

    [Fact]
    public async Task GetDistributionsAsync_Leadership_ComputesAllFour()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, openRequestCount: 2);
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Cas("c0", "Unknown", c => c.Classification = Classification.Unknown));
            db.Cases.Add(Cas("c1", "Review1", c => c.Classification = Classification.ReviewCase));
            db.Cases.Add(Cas("c2", "Review2", c => c.Classification = Classification.ReviewCase));

            db.People.Add(Per("p1", "Subject"));
            db.PersonDocs.Add(new PersonDoc { PersonId = "p1", Outcome = MeasureOutcome.RunningStill, Timestamp = DateTime.UtcNow });
            db.PersonDocs.Add(new PersonDoc { PersonId = "p1", Outcome = MeasureOutcome.Shot, Timestamp = DateTime.UtcNow });

            db.Factions.Add(Fac("fcrit", "Crit", f => f.ThreatScore = 80));
            db.Factions.Add(Fac("flow", "Low", f => f.ThreatScore = 10));
            db.Factions.Add(Fac("fno", "No", f => f.ThreatScore = null));

            db.Users.Add(Seed.Agent("u1", status: AgentStatus.Pending));
            db.Users.Add(Seed.Agent("u2", configure: a => a.NameChangeRequestedAt = DateTime.UtcNow));
            db.Taskforces.Add(Tf("tf1", status: TaskforceStatus.Requested));
            db.AgentPromotionRequests.Add(new AgentPromotionRequest { AgentId = "u1", Status = PromotionStatus.Requested });
            db.SaveChanges();
        }

        var d = await svc.GetDistributionsAsync(isLeadership: true, meId: "lead");

        // Cases by classification: All = [Unknown, ReviewCase, SuspicionCase, SecuredStateThreatening]
        Assert.Equal(4, d.CasesByClassification.Count);
        Assert.Equal(1, d.CasesByClassification[0].Count);   // Unknown
        Assert.Equal(2, d.CasesByClassification[1].Count);   // ReviewCase

        // Measure outcomes: All = [RunningStill, OfficiallyReleased, Injection, Shot]
        Assert.Equal(4, d.MeasureOutcomes.Count);
        Assert.Equal(1, d.MeasureOutcomes[0].Count);         // RunningStill
        Assert.Equal(1, d.MeasureOutcomes[3].Count);         // Shot

        // Factions by hazard: All = [No, Low, Medium, High, Critical]
        Assert.Equal(5, d.FactionsByHazard.Count);
        Assert.Equal(1, d.FactionsByHazard[0].Count);        // No (null score)
        Assert.Equal(1, d.FactionsByHazard[1].Count);        // Low
        Assert.Equal(1, d.FactionsByHazard[4].Count);        // Critical

        // Open requests by kind: [Hochstufung, Registrierung, Namensänderung, Taskforce, Beförderung]
        Assert.Equal(5, d.OpenRequestsByKind.Count);
        Assert.Equal(2, d.OpenRequestsByKind[0].Count);      // upgrades
        Assert.Equal(1, d.OpenRequestsByKind[1].Count);      // pending registration
        Assert.Equal(1, d.OpenRequestsByKind[2].Count);      // name change
        Assert.Equal(1, d.OpenRequestsByKind[3].Count);      // requested taskforce
        Assert.Equal(1, d.OpenRequestsByKind[4].Count);      // promotion
    }

    [Fact]
    public async Task GetDistributionsAsync_NonLeadership_ExcludesClassified()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx, openRequestCount: 0);
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Cas("c1", "Pub", c => c.Classification = Classification.ReviewCase));
            db.Cases.Add(Cas("c2", "Sec", c => { c.Classification = Classification.ReviewCase; c.IsClassified = true; }));

            db.People.Add(Per("p1", "Pub"));
            db.People.Add(Per("p2", "Sec", p => p.IsClassified = true));
            db.PersonDocs.Add(new PersonDoc { PersonId = "p1", Outcome = MeasureOutcome.Shot, Timestamp = DateTime.UtcNow });
            db.PersonDocs.Add(new PersonDoc { PersonId = "p2", Outcome = MeasureOutcome.Shot, Timestamp = DateTime.UtcNow });

            db.Factions.Add(Fac("f1", "Pub", f => f.ThreatScore = 80));
            db.Factions.Add(Fac("f2", "Sec", f => { f.ThreatScore = 80; f.IsClassified = true; }));
            db.SaveChanges();
        }

        var d = await svc.GetDistributionsAsync(isLeadership: false, meId: "me");

        Assert.Equal(1, d.CasesByClassification[1].Count);   // only the unclassified ReviewCase
        Assert.Equal(1, d.MeasureOutcomes[3].Count);         // only the doc under the unclassified person
        Assert.Equal(1, d.FactionsByHazard[4].Count);        // only the unclassified critical faction
    }
}
