using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Who may see a directive, and who owes its acknowledgment; both follow the classification of the row.</summary>
public sealed class DirectiveVisibilityTests
{
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    public static TheoryData<DocumentClassification> Levels()
    {
        var data = new TheoryData<DocumentClassification>();
        foreach (var level in Enum.GetValues<DocumentClassification>())
        {
            data.Add(level);
        }
        return data;
    }

    private static Directive Row(string id, int number, DocumentClassification level = DocumentClassification.None,
        Action<Directive>? configure = null)
    {
        var directive = new Directive
        {
            Id = id,
            Number = number,
            Title = "Anweisung " + id,
            Classification = level,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
            CreatedAt = Stamp,
        };
        configure?.Invoke(directive);
        return directive;
    }

    private static DirectiveAcknowledgment Ack(string directiveId, string agentId, int version)
        => new() { DirectiveId = directiveId, AgentId = agentId, Version = version, AcknowledgedAt = Stamp, CreatedAt = Stamp };

    private static ClaimsPrincipal PrincipalOf(Agent agent)
        => ClaimsPrincipalBuilder.Agent(agent.Id).WithRank(agent.Rank!.Value)
            .AsTru(agent.IsTRU).AsHrb(agent.IsHRB).AsAdmin(agent.IsAdmin).Build();

    private static async Task<List<string>> AgentIdsAsync(IQueryable<DirectiveDebtRow> rows, string directiveId)
        => (await rows.Where(r => r.DirectiveId == directiveId).Select(r => r.AgentId).ToListAsync()).Order().ToList();

    [Theory]
    [MemberData(nameof(Levels))]
    public async Task Each_level_reaches_exactly_its_readers(DocumentClassification level)
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.Add(Row("d1", 1, level));
            await seed.SaveChangesAsync();
        }

        ClaimsPrincipal[] readers =
        [
            ClaimsPrincipalBuilder.Agent("trainee").WithRank(Rank.Trainee).Build(),
            ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().Build(),
            ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build(),
            ClaimsPrincipalBuilder.Agent("chief").WithRank(Rank.SectionChief).Build(),
            ClaimsPrincipalBuilder.Agent("supervision").AsTeamLead().Build(),
            ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director)
                .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
        ];

        await using var db = ctx.NewContext();
        foreach (var reader in readers)
        {
            var scope = ViewerScope.From(reader);
            // partners never read
            var expected = !scope.IsPartner && scope.CanSee(level);

            Assert.Equal(expected, await DirectiveVisibility.IsVisibleAsync(db, "d1", scope));
            Assert.Equal(expected ? 1 : 0, await db.Directives.OnlyVisible(scope).CountAsync());
        }
    }

    [Fact]
    public async Task A_classified_directive_is_hidden_from_an_agent_without_the_level()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.AddRange(
                Row("open", 1),
                Row("lead", 2, DocumentClassification.Leadership),
                Row("tru", 3, DocumentClassification.Tru),
                Row("hrb", 4, DocumentClassification.Hrb));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var trainee = ViewerScope.From(ClaimsPrincipalBuilder.Agent("t").WithRank(Rank.Trainee).Build());
        var tru = ViewerScope.From(ClaimsPrincipalBuilder.Agent("u").WithRank(Rank.SpecialAgent).AsTru().Build());
        var chief = ViewerScope.From(ClaimsPrincipalBuilder.Agent("c").WithRank(Rank.SectionChief).Build());

        Assert.Equal(new[] { "open" }, (await db.Directives.OnlyVisible(trainee).Select(d => d.Id).ToListAsync()).Order());
        Assert.Equal(new[] { "open", "tru" }, (await db.Directives.OnlyVisible(tru).Select(d => d.Id).ToListAsync()).Order());
        Assert.Equal(new[] { "hrb", "lead", "open", "tru" }, (await db.Directives.OnlyVisible(chief).Select(d => d.Id).ToListAsync()).Order());
    }

    [Fact]
    public async Task The_record_gate_answers_a_directive_through_its_own_level()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.AddRange(Row("open", 1), Row("lead", 2, DocumentClassification.Leadership));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var trainee = ViewerScope.From(ClaimsPrincipalBuilder.Agent("t").WithRank(Rank.Trainee).Build());
        var partner = ViewerScope.From(ClaimsPrincipalBuilder.Agent("p").WithRank(Rank.Director)
            .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build());
        var chief = ViewerScope.From(ClaimsPrincipalBuilder.Agent("c").WithRank(Rank.SectionChief).Build());

        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "open", trainee));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "lead", trainee));
        Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "lead", chief));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "open", partner));
    }

    [Fact]
    public async Task A_missing_directive_is_never_visible()
    {
        using var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsAdmin().AsHrb().Build());

        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "gibt-es-nicht", scope));
        Assert.False(await DirectiveVisibility.IsVisibleAsync(db, "gibt-es-nicht", scope));
    }

    [Fact]
    public async Task A_trashed_directive_is_not_visible()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.Add(Row("gone", 1, configure: d => { d.IsDeleted = true; d.DeletedAt = Stamp; }));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsAdmin().Build());

        Assert.False(await DirectiveVisibility.IsVisibleAsync(db, "gone", scope));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(Directive), "gone", scope));
    }

    [Fact]
    public async Task Audience_and_debt_follow_the_level_and_the_acknowledge_from_version()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Users.AddRange(
                Seed.Agent("a", Rank.Trainee),
                Seed.Agent("b", Rank.Trainee),
                Seed.Agent("e", Rank.Trainee),
                Seed.Agent("c", Rank.SectionChief),
                // never selectable
                Seed.Agent("supervision", Rank.Director, configure: a => a.IsTeamLead = true),
                Seed.Agent("leaver", Rank.Trainee, AgentStatus.Terminated),
                Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            seed.Directives.AddRange(
                // 2 asked, 3 newest
                Row("asked", 1, configure: d => { d.CurrentVersion = 3; d.AcknowledgeFromVersion = 2; }),
                Row("repealed", 2, configure: d => { d.RepealedAt = Stamp; d.RepealReason = "überholt"; }),
                Row("trashed", 3, configure: d => { d.IsDeleted = true; d.DeletedAt = Stamp; }),
                Row("lead", 4, DocumentClassification.Leadership));
            seed.DirectiveAcknowledgments.AddRange(
                // too old
                Ack("asked", "a", 1),
                Ack("asked", "b", 2),
                // newer still counts
                Ack("asked", "e", 3));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();

        Assert.Equal(new[] { "a", "b", "c", "e" }, await AgentIdsAsync(DirectiveDebt.Audience(db), "asked"));
        Assert.Equal(new[] { "a", "c" }, await AgentIdsAsync(DirectiveDebt.Owed(db), "asked"));

        // nobody owes these
        Assert.Empty(await AgentIdsAsync(DirectiveDebt.Audience(db), "repealed"));
        Assert.Empty(await AgentIdsAsync(DirectiveDebt.Owed(db), "repealed"));
        Assert.Empty(await AgentIdsAsync(DirectiveDebt.Audience(db), "trashed"));
        Assert.Empty(await AgentIdsAsync(DirectiveDebt.Owed(db), "trashed"));

        // leadership only
        Assert.Equal(new[] { "c" }, await AgentIdsAsync(DirectiveDebt.Owed(db), "lead"));
    }

    [Fact]
    public async Task Audience_and_visibility_agree_for_every_level_and_agent()
    {
        using var ctx = new SqliteTestContext();
        var agents = new List<Agent>
        {
            Seed.Agent("trainee", Rank.Trainee),
            Seed.Agent("tru", Rank.Trainee, configure: a => a.IsTRU = true),
            Seed.Agent("hrb", Rank.Trainee, configure: a => a.IsHRB = true),
            Seed.Agent("both", Rank.SpecialAgent, configure: a => { a.IsTRU = true; a.IsHRB = true; }),
            Seed.Agent("sac", Rank.SpecialAgentInCharge),
            Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("director", Rank.Director),
            Seed.Agent("admin", Rank.Trainee, configure: a => a.IsAdmin = true),
        };
        var directiveIds = new Dictionary<DocumentClassification, string>();
        await using (var seed = ctx.NewContext())
        {
            seed.Users.AddRange(agents);
            foreach (var level in Enum.GetValues<DocumentClassification>())
            {
                var id = "d-" + level;
                directiveIds[level] = id;
                seed.Directives.Add(Row(id, (int)level + 1, level));
            }
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var owed = new List<bool>();
        foreach (var (level, directiveId) in directiveIds)
        {
            foreach (var agent in agents)
            {
                var agentId = agent.Id;
                var inAudience = await DirectiveDebt.Audience(db)
                    .AnyAsync(r => r.DirectiveId == directiveId && r.AgentId == agentId);
                var visible = await db.Directives.OnlyVisible(ViewerScope.From(PrincipalOf(agent)))
                    .AnyAsync(d => d.Id == directiveId);

                Assert.True(inAudience == visible, $"{level} / {agentId}: audience {inAudience}, visibility {visible}");
                owed.Add(inAudience);
            }
        }

        // not vacuous
        Assert.Contains(true, owed);
        Assert.Contains(false, owed);
    }
}
