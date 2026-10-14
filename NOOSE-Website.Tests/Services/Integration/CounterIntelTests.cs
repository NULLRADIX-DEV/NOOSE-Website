using System.Security.Claims;
using NOOSE_Website.Data.Entities.CounterIntel;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Tests for the counter-intelligence cockpit and the rule CRUD behind it.</summary>
public sealed class CounterIntelTests
{
    private static ClaimsPrincipal Leader() => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();
    private static ClaimsPrincipal Junior() => ClaimsPrincipalBuilder.Agent("low").WithRank(Rank.JuniorAgent).Build();

    private static CounterIntelRuleInput Input(Action<CounterIntelRuleDefinition>? configure = null, string name = "Testregel")
    {
        var definition = new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 3 };
        configure?.Invoke(definition);
        return new CounterIntelRuleInput { Name = name, Definition = definition };
    }

    private static CounterIntelService Cockpit(SqliteTestContext ctx)
        => new(ctx.Factory, new CounterIntelRuleService(ctx.Factory));

    // ==================== CounterIntelService ====================

    [Fact]
    public async Task GetOverviewAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Cockpit(ctx).GetOverviewAsync(Junior()));
    }

    [Fact]
    public async Task GetOverviewAsync_CountsRecentAccesses()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 3; i++)
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = "a1", AgentName = "A", Timestamp = DateTime.UtcNow.AddHours(-1),
                    EntityType = "Person", EntityId = $"p{i}",
                });
            }
            db.SaveChanges();
        }
        var overview = await Cockpit(ctx).GetOverviewAsync(Leader());

        Assert.Equal(3, overview.TotalAccesses);
        Assert.Equal(1, overview.DistinctAgents);
        Assert.Equal(3, overview.DistinctRecords);
    }

    [Fact]
    public async Task GetFlagsAsync_ReturnsNothing_WithoutRules()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 50; i++)
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = "a1", AgentName = "A", Timestamp = DateTime.UtcNow.AddMinutes(-i),
                    EntityType = "Person", EntityId = $"p{i}",
                });
            }
            db.SaveChanges();
        }

        Assert.Empty(await Cockpit(ctx).GetFlagsAsync(Leader()));
    }

    [Fact]
    public async Task LoadAsync_DeniesAPersonnelFileToCitizensAndApplicantsAlike()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("act", status: AgentStatus.Active),
                Seed.Agent("civ", status: AgentStatus.Civilian),
                Seed.Agent("app", status: AgentStatus.Applicant));
            foreach (var id in new[] { "act", "civ", "app" })
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = id, AgentName = id, Timestamp = DateTime.UtcNow.AddMinutes(-1),
                    EntityType = "Person", EntityId = "p1",
                });
            }
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        var events = await CounterIntelEventLoader.LoadAsync(
            read, [new CounterIntelRuleDefinition { WindowDays = 30, Threshold = 1 }]);

        // a citizen who applies keeps no personnel file, so the flag must not start linking to one
        Assert.True(events.Single(e => e.AgentId == "civ").ActorHasNoPersonnelFile);
        Assert.True(events.Single(e => e.AgentId == "app").ActorHasNoPersonnelFile);
        Assert.False(events.Single(e => e.AgentId == "act").ActorHasNoPersonnelFile);
    }

    [Fact]
    public async Task GetFlagsAsync_ExcludesReadOnlySupervisors()
    {
        using var ctx = new SqliteTestContext();
        var rules = new CounterIntelRuleService(ctx.Factory);
        await rules.CreateAsync(Input(d => d.Threshold = 40, "Massen-Zugriff"), Leader());

        var noon = DateTime.UtcNow.Date.AddHours(12);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("normal"));
            db.Users.Add(Seed.Agent("reader", configure: a => a.IsTeamLead = true)); // OnlyReader
            for (var i = 0; i < 45; i++)
            {
                db.AccessLogs.Add(new AccessLog { AgentId = "normal", AgentName = "Normal", Timestamp = noon.AddSeconds(i), EntityType = "Person", EntityId = $"n{i}" });
                db.AccessLogs.Add(new AccessLog { AgentId = "reader", AgentName = "Reader", Timestamp = noon.AddSeconds(i), EntityType = "Person", EntityId = $"r{i}" });
            }
            db.SaveChanges();
        }
        var flags = await Cockpit(ctx).GetFlagsAsync(Leader());

        Assert.Contains(flags, f => f.AgentId == "normal");
        Assert.DoesNotContain(flags, f => f.AgentId == "reader");
    }

    [Fact]
    public async Task GetFlagsAsync_ReadsWriteActionsFromTheAuditLog()
    {
        using var ctx = new SqliteTestContext();
        var rules = new CounterIntelRuleService(ctx.Factory);
        await rules.CreateAsync(Input(d =>
        {
            d.Actions = [CounterIntelActionKind.Deleted];
            d.Threshold = 3;
        }, "Lösch-Serie"), Leader());

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("shredder"));
            for (var i = 0; i < 4; i++)
            {
                db.AuditLogs.Add(new AuditLog
                {
                    AgentId = "shredder", AgentName = "Shredder", Timestamp = DateTime.UtcNow.AddMinutes(-i),
                    EntityType = "Person", EntityId = $"p{i}", Action = AuditAction.Deleted,
                });
                // reads must not count towards a delete rule
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = "shredder", AgentName = "Shredder", Timestamp = DateTime.UtcNow.AddMinutes(-i),
                    EntityType = "Person", EntityId = $"r{i}",
                });
            }
            db.SaveChanges();
        }

        var flag = Assert.Single(await Cockpit(ctx).GetFlagsAsync(Leader()));
        Assert.Equal("shredder", flag.AgentId);
        Assert.Equal(4, flag.Severity);
    }

    [Fact]
    public async Task GetFlagsAsync_ClassifiedOnlyRule_IgnoresOpenRecords()
    {
        using var ctx = new SqliteTestContext();
        var rules = new CounterIntelRuleService(ctx.Factory);
        await rules.CreateAsync(Input(d =>
        {
            d.ClassifiedOnly = true;
            d.Threshold = 2;
        }, "VS-Wühlerei"), Leader());

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("snoop"));
            var secret = Seed.Person(configure: p => p.IsClassified = true);
            var secret2 = Seed.Person(configure: p => p.IsClassified = true);
            var open = Seed.Person();
            var open2 = Seed.Person();
            db.People.AddRange(secret, secret2, open, open2);
            foreach (var p in new[] { secret, secret2, open, open2 })
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = "snoop", AgentName = "Snoop", Timestamp = DateTime.UtcNow.AddMinutes(-1),
                    EntityType = nameof(NOOSE_Website.Data.Entities.People.Person), EntityId = p.Id,
                });
            }
            db.SaveChanges();
        }

        var flag = Assert.Single(await Cockpit(ctx).GetFlagsAsync(Leader()));
        Assert.Equal(2, flag.Severity); // only the two classified files counted
    }

    [Fact]
    public async Task GetAgentsAsync_ResolvesNamesFromRoster_AndSkipsBlanksAndSupervisors()
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
            foreach (var id in new[] { "a1", "supervisor", "applicant" })
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = id, AgentName = string.Empty, Timestamp = DateTime.UtcNow.AddHours(-1),
                    EntityType = "Person", EntityId = "p1",
                });
            }
            db.SaveChanges();
        }

        var agents = await Cockpit(ctx).GetAgentsAsync(Leader());

        Assert.Equal("Falke", Assert.Single(agents).Name);
    }

    [Fact]
    public async Task GetAgentOptionsAsync_ExcludesTeamLeadAdminsAndPartners_ButKeepsTerminated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Falke"));
            // a former agent stays listed: rules are written about what they did
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated,
                configure: a => a.Codename = "Ehemalig"));
            db.Users.Add(Seed.Agent("tl", configure: a => { a.Codename = "Aufsicht"; a.IsTeamLead = true; }));
            db.Users.Add(Seed.Agent("tl-adm", configure: a =>
            {
                a.Codename = "Chef";
                a.IsTeamLead = true;
                a.IsAdmin = true;
            }));
            db.Users.Add(Seed.Agent("p1", configure: a =>
            {
                a.Codename = "Extern";
                a.PartnerAgency = PartnerAgency.LSPD;
            }));
            db.SaveChanges();
        }

        var options = await new CounterIntelRuleService(ctx.Factory).GetAgentOptionsAsync(Leader());

        Assert.Equal(new[] { "Ehemalig", "Falke" }, options.Select(o => o.Name).ToArray());
    }

    [Fact]
    public async Task GetAgentsAsync_SkipsAgentsWithoutAccessRows()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Falke"));
            db.Users.Add(Seed.Agent("quiet", configure: a => a.Codename = "Bussard"));
            db.AccessLogs.Add(new AccessLog
            {
                AgentId = "a1", AgentName = "Falke", Timestamp = DateTime.UtcNow.AddHours(-1),
                EntityType = "Person", EntityId = "p1",
            });
            db.SaveChanges();
        }

        var agents = await Cockpit(ctx).GetAgentsAsync(Leader());

        Assert.Equal("Falke", Assert.Single(agents).Name);
    }

    // ==================== CounterIntelRuleService ====================

    [Theory]
    [InlineData("create")]
    [InlineData("read")]
    [InlineData("update")]
    [InlineData("toggle")]
    [InlineData("duplicate")]
    [InlineData("delete")]
    [InlineData("defaults")]
    public async Task RuleService_Throws_ForNonLeadership(string operation)
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);

        Func<Task> act = operation switch
        {
            "create" => () => svc.CreateAsync(Input(), Junior()),
            "read" => () => svc.GetAllAsync(Junior()),
            "update" => () => svc.UpdateAsync("x", Input(), Junior()),
            "toggle" => () => svc.SetActiveAsync("x", false, Junior()),
            "duplicate" => () => svc.DuplicateAsync("x", Junior()),
            "delete" => () => svc.DeleteAsync("x", Junior()),
            _ => () => svc.RestoreDefaultsAsync(Junior()),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(act);
    }

    [Fact]
    public async Task RuleService_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var supervisor = ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => new CounterIntelRuleService(ctx.Factory).CreateAsync(Input(), supervisor));
    }

    [Fact]
    public async Task RuleService_RoundTripsTheDefinition()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        var id = await svc.CreateAsync(Input(d =>
        {
            d.Actions = [CounterIntelActionKind.Read, CounterIntelActionKind.Deleted];
            d.EntityTypes = ["Person", "Faction"];
            d.ClassifiedOnly = true;
            d.ActorRanks = [Rank.JuniorAgent];
            d.FromHour = 22;
            d.ToHour = 6;
            d.Bucket = CounterIntelBucket.Sliding;
            d.SlidingMinutes = 45;
            d.Threshold = 7;
        }), Leader());

        var rule = Assert.Single(await svc.GetAllAsync(Leader()));

        Assert.Equal(id, rule.Id);
        Assert.Equal([CounterIntelActionKind.Read, CounterIntelActionKind.Deleted], rule.Definition.Actions);
        Assert.Equal(["Person", "Faction"], rule.Definition.EntityTypes);
        Assert.True(rule.Definition.ClassifiedOnly);
        Assert.Equal([Rank.JuniorAgent], rule.Definition.ActorRanks);
        Assert.Equal(22, rule.Definition.FromHour);
        Assert.Equal(45, rule.Definition.SlidingMinutes);
        Assert.Equal(7, rule.Definition.Threshold);
    }

    [Fact]
    public async Task RuleService_GetActive_SkipsInactiveAndUnparsableRules()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        await svc.CreateAsync(Input(name: "aktiv"), Leader());
        var offId = await svc.CreateAsync(Input(name: "aus"), Leader());
        await svc.SetActiveAsync(offId, false, Leader());

        using (var db = ctx.NewContext())
        {
            db.CounterIntelRules.Add(new CounterIntelRule { Name = "kaputt", DefinitionJson = "{ nope" });
            db.SaveChanges();
        }

        Assert.Equal("aktiv", Assert.Single(await svc.GetActiveAsync()).Name);
    }

    [Fact]
    public async Task RuleService_DuplicateStartsInactive()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        var id = await svc.CreateAsync(Input(name: "Original"), Leader());

        var copyId = await svc.DuplicateAsync(id, Leader());
        var copy = (await svc.GetAllAsync(Leader())).Single(r => r.Id == copyId);

        Assert.Equal("Original (Kopie)", copy.Name);
        Assert.False(copy.IsActive);
    }

    // every way a rule comes into being starts its bell at that moment, never earlier
    [Fact]
    public async Task RuleService_ArmsTheBellWheneverARuleComesIntoBeing()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        var before = DateTime.UtcNow;

        var id = await svc.CreateAsync(Input(name: "Original"), Leader());
        var copyId = await svc.DuplicateAsync(id, Leader());
        await svc.RestoreDefaultsAsync(Leader());

        var all = await svc.GetAllAsync(Leader());
        Assert.All(all, r => Assert.True(r.ReportFrom >= before, r.Name));
        Assert.All(all, r => Assert.True(r.Notify, r.Name));
        Assert.Contains(all, r => r.Id == copyId);
    }

    [Fact]
    public async Task RuleService_DuplicateKeepsASilencedBellSilent()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        var input = Input(name: "Leise");
        input.Notify = false;
        var id = await svc.CreateAsync(input, Leader());

        var copyId = await svc.DuplicateAsync(id, Leader());

        Assert.False((await svc.GetAllAsync(Leader())).Single(r => r.Id == copyId).Notify);
    }

    [Fact]
    public async Task RuleService_ReviveFromTheBinArmsTheBellAnew()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        await svc.RestoreDefaultsAsync(Leader());
        using (var db = ctx.NewContext())
        {
            // the bin as the interceptor would leave it
            var row = db.CounterIntelRules.Single(r => r.Id == CounterIntelRuleDefaults.MassAccessId);
            row.IsDeleted = true;
            row.ReportFrom = DateTime.UtcNow.AddDays(-3);
            db.SaveChanges();
        }
        var revived = DateTime.UtcNow;

        Assert.Equal(1, await svc.RestoreDefaultsAsync(Leader()));

        var rule = (await svc.GetAllAsync(Leader())).Single(r => r.Id == CounterIntelRuleDefaults.MassAccessId);
        Assert.True(rule.ReportFrom >= revived);
    }

    // the Remove → soft-delete rewrite belongs to the interceptor, which the test context does not wire up;
    // what is testable here is that a deleted rule stops being listed and stops being evaluated
    [Fact]
    public async Task RuleService_DeleteHidesTheRuleEverywhere()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);
        var id = await svc.CreateAsync(Input(), Leader());

        await svc.DeleteAsync(id, Leader());

        Assert.Empty(await svc.GetAllAsync(Leader()));
        Assert.Empty(await svc.GetActiveAsync());
    }

    [Fact]
    public async Task RuleService_DeleteThrows_ForAnUnknownRule()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CounterIntelRuleService(ctx.Factory).DeleteAsync("gibt-es-nicht", Leader()));
    }

    [Fact]
    public async Task RuleService_RestoreDefaults_AddsThenRevivesThemWithoutDuplicating()
    {
        using var ctx = new SqliteTestContext();
        var svc = new CounterIntelRuleService(ctx.Factory);

        Assert.Equal(CounterIntelRuleDefaults.All.Count, await svc.RestoreDefaultsAsync(Leader()));
        Assert.Equal(0, await svc.RestoreDefaultsAsync(Leader()));

        await svc.DeleteAsync(CounterIntelRuleDefaults.OffHoursId, Leader());
        Assert.Equal(1, await svc.RestoreDefaultsAsync(Leader()));
        Assert.Equal(CounterIntelRuleDefaults.All.Count, (await svc.GetAllAsync(Leader())).Count);
    }

    [Theory]
    [InlineData(0, 30, 0, 0)]      // threshold below 1
    [InlineData(3, 0, 0, 0)]       // window below 1
    [InlineData(3, 200, 0, 0)]     // window above the cap
    [InlineData(3, 30, 24, 0)]     // hour out of range
    [InlineData(3, 30, 0, 25)]     // hour out of range
    public async Task RuleService_RejectsInvalidDefinitions(int threshold, int windowDays, int fromHour, int toHour)
    {
        using var ctx = new SqliteTestContext();
        var input = Input(d =>
        {
            d.Threshold = threshold;
            d.WindowDays = windowDays;
            d.FromHour = fromHour;
            d.ToHour = toHour;
        });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CounterIntelRuleService(ctx.Factory).CreateAsync(input, Leader()));
    }

    [Fact]
    public async Task RuleService_RejectsABlankName()
    {
        using var ctx = new SqliteTestContext();
        var input = Input(name: "   ");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new CounterIntelRuleService(ctx.Factory).CreateAsync(input, Leader()));
    }
}
