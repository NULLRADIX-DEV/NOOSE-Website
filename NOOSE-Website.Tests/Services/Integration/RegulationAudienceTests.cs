using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Who reads the regulation and who owes a paragraph: the SQL audience query against its in-memory twin.</summary>
public sealed class RegulationAudienceTests
{
    private static async Task<List<string>> AgentIdsAsync(IQueryable<RegulationDebtRow> rows, string paragraphId)
        => (await rows.Where(r => r.ParagraphId == paragraphId).Select(r => r.AgentId).ToListAsync()).Order().ToList();

    [Fact]
    public async Task The_sql_audience_agrees_with_InAudience_for_every_rank_division_and_flag()
    {
        using var ctx = new SqliteTestContext();
        var agents = new List<Agent>();
        var n = 0;
        foreach (var row in NOOSE_Website.Tests.Services.RegulationRulesTests.Combinations())
        {
            var (rank, division, tru, hrb) = ((Rank?)row[0], (Division?)row[1], (bool)row[2]!, (bool)row[3]!);
            agents.Add(Seed.Agent($"a{n++}", configure: a =>
            {
                a.Rank = rank;
                a.Division = division;
                a.IsTRU = tru;
                a.IsHRB = hrb;
            }));
        }
        await using (var seed = ctx.NewContext())
        {
            seed.Users.AddRange(agents);
            seed.RegulationSections.Add(SectionRow("s1", 10));
            foreach (var audience in RegulationAudienceDisplay.All)
            {
                seed.RegulationParagraphs.Add(ParagraphRow("p-" + audience, "s1", audience: audience));
            }
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var rows = await RegulationDebt.Audience(db).Select(r => new { r.ParagraphId, r.AgentId }).ToListAsync();
        var inSql = rows.Select(r => (r.ParagraphId, r.AgentId)).ToHashSet();
        var owed = 0;
        foreach (var audience in RegulationAudienceDisplay.All)
        {
            foreach (var agent in agents)
            {
                var expected = RegulationRules.InAudience(audience, DivisionRules.Of(agent), agent.IsHRB);
                Assert.True(expected == inSql.Contains(("p-" + audience, agent.Id)),
                    $"{audience} / {agent.Rank?.ToString() ?? "-"} / {agent.Division?.ToString() ?? "-"} / tru {agent.IsTRU} / hrb {agent.IsHRB}");
                // and both agree with the spec
                Assert.Equal(NOOSE_Website.Tests.Services.RegulationRulesTests.Owes(audience, agent.Rank, agent.Division, agent.IsTRU, agent.IsHRB),
                    expected);
                owed += expected ? 1 : 0;
            }
        }
        // no duplicates, not vacuous
        Assert.Equal(rows.Count, inSql.Count);
        Assert.Equal(owed, rows.Count);
        Assert.InRange(owed, 1, agents.Count * RegulationAudienceDisplay.All.Count - 1);
    }

    [Fact]
    public async Task Only_selectable_staff_is_in_any_audience()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Users.AddRange(
                Seed.Agent("plain", Rank.Trainee),
                Seed.Agent("sup", Rank.Director, configure: a => { a.IsTeamLead = true; a.IsHRB = true; }),
                Seed.Agent("leaver", Rank.Trainee, AgentStatus.Terminated, a => a.IsHRB = true),
                Seed.Agent("partner", Rank.Director, configure: a => { a.PartnerAgency = PartnerAgency.LSPD; a.IsHRB = true; }));
            seed.RegulationSections.Add(SectionRow("s1", 10));
            seed.RegulationParagraphs.AddRange(
                ParagraphRow("bureau", "s1"),
                ParagraphRow("admin", "s1", audience: RegulationAudience.Administration),
                ParagraphRow("cid", "s1", audience: RegulationAudience.Cid));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        Assert.Equal(["plain"], await AgentIdsAsync(RegulationDebt.Audience(db), "bureau"));
        Assert.Empty(await AgentIdsAsync(RegulationDebt.Audience(db), "admin"));
        Assert.Equal(["plain"], await AgentIdsAsync(RegulationDebt.Audience(db), "cid"));
    }

    [Fact]
    public async Task Debt_follows_the_acknowledge_from_version_and_ends_on_repeal_and_trash()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Users.AddRange(
                Seed.Agent("a", Rank.Trainee),
                Seed.Agent("b", Rank.Trainee),
                Seed.Agent("e", Rank.Trainee),
                Seed.Agent("c", Rank.SectionChief));
            seed.RegulationSections.AddRange(
                SectionRow("s1", 10),
                SectionRow("s-gone", 20, configure: s => { s.IsDeleted = true; s.DeletedAt = Stamp; }));
            seed.RegulationParagraphs.AddRange(
                // 2 asked, 3 newest
                ParagraphRow("asked", "s1", configure: p => { p.CurrentVersion = 3; p.AcknowledgeFromVersion = 2; }),
                ParagraphRow("repealed", "s1", configure: p => { p.RepealedAt = Stamp; p.RepealReason = "überholt"; }),
                ParagraphRow("trashed", "s1", configure: p => { p.IsDeleted = true; p.DeletedAt = Stamp; }),
                ParagraphRow("orphaned", "s-gone"));
            seed.RegulationAcknowledgments.AddRange(
                // too old
                AckRow("asked", "a", 1),
                AckRow("asked", "b", 2),
                // newer still counts
                AckRow("asked", "e", 3));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();

        Assert.Equal(["a", "b", "c", "e"], await AgentIdsAsync(RegulationDebt.Audience(db), "asked"));
        Assert.Equal(["a", "c"], await AgentIdsAsync(RegulationDebt.Owed(db), "asked"));

        // nobody owes these
        foreach (var id in new[] { "repealed", "trashed", "orphaned" })
        {
            Assert.Empty(await AgentIdsAsync(RegulationDebt.Audience(db), id));
            Assert.Empty(await AgentIdsAsync(RegulationDebt.Owed(db), id));
        }
        Assert.Equal(["asked"], await RegulationDebt.Live(db).Select(p => p.Id).ToListAsync());
    }

    [Fact]
    public async Task Every_internal_agent_reads_every_paragraph_a_partner_none()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.Add(SectionRow("s1", 10));
            foreach (var audience in RegulationAudienceDisplay.All)
            {
                seed.RegulationParagraphs.Add(ParagraphRow("p-" + audience, "s1", audience: audience));
            }
            seed.RegulationParagraphs.Add(ParagraphRow("repealed", "s1", configure: p => p.RepealedAt = Stamp));
            await seed.SaveChangesAsync();
        }

        ClaimsPrincipal[] readers =
        [
            Plain("trainee"),
            ClaimsPrincipalBuilder.Agent("tru").WithRank(Rank.SpecialAgent).AsTru().AsDivision(Division.Hrt).Build(),
            ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build(),
            Chief("chief", Division.Ctd),
            Supervision(),
        ];

        await using var db = ctx.NewContext();
        foreach (var reader in readers)
        {
            var scope = ViewerScope.From(reader);
            // no classification: the audience decides only who owes, never who reads
            Assert.Equal(6, await db.RegulationParagraphs.OnlyVisible(db, scope).CountAsync());
            Assert.True(await RegulationVisibility.IsVisibleAsync(db, "p-Hrt", scope));
            Assert.True(await Visibility.IsRecordVisibleAsync(db, nameof(RegulationParagraph), "p-Administration", scope));
        }

        var partner = ViewerScope.From(Partner());
        Assert.Equal(0, await db.RegulationParagraphs.OnlyVisible(db, partner).CountAsync());
        Assert.False(await RegulationVisibility.IsVisibleAsync(db, "p-Bureau", partner));
        Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(RegulationParagraph), "p-Bureau", partner));
        // a hand-built scope never reads
        Assert.Equal(0, await db.RegulationParagraphs.OnlyVisible(db, new ViewerScope(true, true, "x", null)).CountAsync());
    }

    [Fact]
    public async Task A_missing_trashed_or_orphaned_paragraph_is_never_visible()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.AddRange(
                SectionRow("s1", 10),
                SectionRow("s-gone", 20, configure: s => { s.IsDeleted = true; s.DeletedAt = Stamp; }));
            seed.RegulationParagraphs.AddRange(
                ParagraphRow("gone", "s1", configure: p => { p.IsDeleted = true; p.DeletedAt = Stamp; }),
                ParagraphRow("orphaned", "s-gone"));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var scope = ViewerScope.From(ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).AsAdmin().AsHrb().Build());

        foreach (var id in new[] { "gibt-es-nicht", "gone", "orphaned" })
        {
            Assert.False(await RegulationVisibility.IsVisibleAsync(db, id, scope), id);
            Assert.False(await Visibility.IsRecordVisibleAsync(db, nameof(RegulationParagraph), id, scope), id);
        }
    }

    [Fact]
    public async Task Section_numbers_skip_the_foreword_and_trashed_sections()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.RegulationSections.AddRange(
                SectionRow("second", 20, "Zweiter"),
                SectionRow("first", 10, "Erster"),
                // sorted last, still shown first
                SectionRow("preamble", 99, "Vorwort", preamble: true),
                SectionRow("gone", 15, "Weg", configure: s => { s.IsDeleted = true; s.DeletedAt = Stamp; }));
            seed.RegulationParagraphs.AddRange(
                ParagraphRow("p1", "second", "Funkdisziplin"),
                ParagraphRow("p0", "preamble", "Leitbild"));
            await seed.SaveChangesAsync();
        }

        await using var db = ctx.NewContext();
        var numbers = await RegulationVisibility.SectionNumbersAsync(db);

        Assert.Equal(["preamble", "first", "second"], numbers.Keys);
        Assert.Null(numbers["preamble"].Number);
        Assert.Equal(1, numbers["first"].Number);
        Assert.Equal(2, numbers["second"].Number);
        var labels = await RegulationVisibility.LabelsAsync(db, ["p1", "p0"]);
        Assert.Equal("DVO Abschnitt 2 · Funkdisziplin", labels["p1"]);
        Assert.Equal("DVO Vorwort · Leitbild", labels["p0"]);
        Assert.Empty(await RegulationVisibility.LabelsAsync(db, []));
    }
}
