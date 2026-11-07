using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Badge, shift list, page and quota read one debt predicate; they must never disagree.</summary>
public sealed class RegulationDebtTests
{
    public static TheoryData<RegulationAudience> Audiences()
    {
        var data = new TheoryData<RegulationAudience>();
        foreach (var audience in RegulationAudienceDisplay.All)
        {
            data.Add(audience);
        }
        return data;
    }

    /// <summary>One agent as the database holds it and as the session carries it.</summary>
    private sealed record Member(Agent Account, ClaimsPrincipal Principal);

    private static Member M(Agent account) => new(account, PrincipalOf(account));

    /// <summary>Every kind of account the debt could wrongly catch or miss.</summary>
    private static List<Member> Cast() =>
    [
        M(Seed.Agent("cid", Rank.Trainee, configure: a => a.Division = Division.Cid)),
        M(Seed.Agent("ctd", Rank.SpecialAgent, configure: a => a.Division = Division.Ctd)),
        M(Seed.Agent("hrt", Rank.SpecialAgent, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; })),
        // legacy rows without a division column
        M(Seed.Agent("legacy-tru", Rank.SeniorSpecialAgent, configure: a => a.IsTRU = true)),
        M(Seed.Agent("legacy", Rank.SpecialAgentInCharge)),
        // HRT below Special Agent sits in the CID
        M(Seed.Agent("young-hrt", Rank.Trainee, configure: a => { a.Division = Division.Hrt; a.IsTRU = true; })),
        M(Seed.Agent("hrb", Rank.SpecialAgent, configure: a => { a.Division = Division.Ctd; a.IsHRB = true; })),
        M(Seed.Agent("chief", Rank.SectionChief, configure: a => a.Division = Division.Hrt)),
        // the Board owes no division paragraph
        M(Seed.Agent("ad", Rank.AssistantDirector, configure: a => { a.Division = Division.Cid; a.IsHRB = true; })),
        M(Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true)),
        M(Seed.Agent("partner", Rank.Director, configure: a => a.PartnerAgency = PartnerAgency.LSPD)),
        M(Seed.Agent("gone", Rank.Trainee, AgentStatus.Terminated)),
    ];

    /// <summary>Who is in the audience, the publisher "boss" (Director, Board) included.</summary>
    private static string[] AudienceOf(RegulationAudience audience) => audience switch
    {
        RegulationAudience.Administration => ["ad", "hrb"],
        RegulationAudience.Cid => ["cid", "legacy", "young-hrt"],
        RegulationAudience.Ctd => ["ctd", "hrb"],
        RegulationAudience.Hrt => ["chief", "hrt", "legacy-tru"],
        _ => ["ad", "boss", "chief", "cid", "ctd", "hrb", "hrt", "legacy", "legacy-tru", "young-hrt"],
    };

    /// <summary>The audience minus the publisher, who acknowledged his own version.</summary>
    private static string[] OwedBy(RegulationAudience audience) => AudienceOf(audience).Where(id => id != "boss").ToArray();

    private static async Task AssertAgreementAsync(RegulationService svc, List<Member> cast, string paragraphId,
        string[] audience, string[] owed, string message)
    {
        var quota = await svc.GetQuotaAsync(paragraphId, Boss());
        Assert.NotNull(quota);
        Assert.True(quota.Total == audience.Length, $"{message}: total {quota.Total}");
        Assert.Equal(owed.Order(), quota.Missing.Select(m => m.AgentId).Order());
        Assert.Equal(audience.Except(owed).Order(), quota.Done.Select(d => d.AgentId).Order());
        Assert.Equal(audience.Length - owed.Length, quota.Acknowledged);

        foreach (var person in cast)
        {
            var id = person.Account.Id;
            var expected = owed.Contains(id) ? 1 : 0;
            var badge = await svc.GetOpenCountAsync(person.Principal);
            var shift = await svc.GetOpenForMeAsync(person.Principal);
            var document = await svc.GetDocumentAsync(person.Principal);
            var detail = await svc.GetParagraphAsync(paragraphId, null, person.Principal);
            var listed = quota.Missing.Count(m => m.AgentId == id);

            Assert.True(badge == expected, $"{message} / {id}: badge {badge}, expected {expected}");
            Assert.True(shift.Count == expected, $"{message} / {id}: shift {shift.Count}, expected {expected}");
            Assert.True(document.OpenCount == expected, $"{message} / {id}: page {document.OpenCount}, expected {expected}");
            Assert.True(document.OpenSeen.Count == expected, $"{message} / {id}: seen {document.OpenSeen.Count}, expected {expected}");
            Assert.True((detail?.MustAcknowledge == true ? 1 : 0) == expected, $"{message} / {id}: detail, expected {expected}");
            Assert.True(listed == expected, $"{message} / {id}: quota {listed}, expected {expected}");
        }
    }

    [Theory]
    [MemberData(nameof(Audiences))]
    public async Task Badge_shift_page_and_quota_agree_for_every_audience(RegulationAudience audience)
    {
        using var ctx = new SqliteTestContext();
        var cast = Cast();
        await SeedAgentsAsync(ctx, [Seed.Agent("boss", Rank.Director), .. cast.Select(p => p.Account)]);
        var section = await SeedSectionAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateParagraphAsync(section, Input(audience: audience), Boss());
        var members = AudienceOf(audience);
        var owed = OwedBy(audience);

        await AssertAgreementAsync(svc, cast, id, members, owed, $"{audience} fresh");

        // everyone acknowledges, and the numbers fall together
        foreach (var person in cast.Where(p => owed.Contains(p.Account.Id)))
        {
            await svc.AcknowledgeAsync(id, 1, person.Principal);
        }
        await AssertAgreementAsync(svc, cast, id, members, [], $"{audience} acknowledged");

        // a version that asks again puts the same agents back on the list
        await svc.PublishVersionAsync(id, Next(1, audience), Boss());
        await AssertAgreementAsync(svc, cast, id, members, owed, $"{audience} asked again");

        // a correction asks nothing, even of the ones who never acknowledged
        var first = cast.First(p => owed.Contains(p.Account.Id));
        await svc.AcknowledgeAsync(id, 2, first.Principal);
        await svc.PublishVersionAsync(id, Next(2, audience, requires: false), Boss());
        await AssertAgreementAsync(svc, cast, id, members, owed.Where(o => o != first.Account.Id).ToArray(),
            $"{audience} corrected");
    }

    [Fact]
    public async Task Joining_or_leaving_a_division_or_the_administration_moves_the_debt()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var section = await SeedSectionAsync(ctx);
        var (svc, _, _) = Build(ctx);
        var bureau = await svc.CreateParagraphAsync(section, Input("Für alle"), Boss());
        var cid = await svc.CreateParagraphAsync(section, Input("Nur CID", RegulationAudience.Cid), Boss());
        var ctd = await svc.CreateParagraphAsync(section, Input("Nur CTD", RegulationAudience.Ctd), Boss());
        var hrt = await svc.CreateParagraphAsync(section, Input("Nur HRT", RegulationAudience.Hrt), Boss());
        var admin = await svc.CreateParagraphAsync(section, Input("Nur Administration", RegulationAudience.Administration), Boss());
        var me = Plain("newbie");

        async Task<string[]> OpenAsync() => (await svc.GetOpenForMeAsync(me)).Select(o => o.ParagraphId).Order().ToArray();
        async Task<bool> MissingAsync(string paragraphId)
            => (await svc.GetQuotaAsync(paragraphId, Boss()))!.Missing.Any(m => m.AgentId == "newbie");

        // a newcomer owes what already stands, without any snapshot; no division column means CID
        await SeedAgentsAsync(ctx, Seed.Agent("newbie", Rank.Trainee));
        Assert.Equal(new[] { bureau, cid }.Order(), await OpenAsync());
        Assert.Equal(2, await svc.GetOpenCountAsync(me));

        // the move to the CTD takes the CID debt along
        await SetAgentAsync(ctx, "newbie", a => a.Division = Division.Ctd);
        Assert.Equal(new[] { bureau, ctd }.Order(), await OpenAsync());
        Assert.True(await MissingAsync(ctd));
        Assert.False(await MissingAsync(cid));

        // the HRT needs a Special Agent: a trainee there still sits in the CID
        await SetAgentAsync(ctx, "newbie", a => { a.Division = Division.Hrt; a.IsTRU = true; });
        Assert.Equal(new[] { bureau, cid }.Order(), await OpenAsync());
        await SetAgentAsync(ctx, "newbie", a => a.Rank = Rank.SpecialAgent);
        Assert.Equal(new[] { bureau, hrt }.Order(), await OpenAsync());

        // the Administration flag adds its paragraphs on top
        await SetAgentAsync(ctx, "newbie", a => a.IsHRB = true);
        Assert.Equal(new[] { admin, bureau, hrt }.Order(), await OpenAsync());
        Assert.Equal(3, await svc.GetOpenCountAsync(me));
        Assert.True(await MissingAsync(admin));

        // a seat on the Board ends every division debt but keeps bureau and administration
        await SetAgentAsync(ctx, "newbie", a => a.Rank = Rank.AssistantDirector);
        Assert.Equal(new[] { admin, bureau }.Order(), await OpenAsync());
        Assert.False(await MissingAsync(hrt));

        // losing the flag ends it again
        await SetAgentAsync(ctx, "newbie", a => a.IsHRB = false);
        Assert.Equal([bureau], await OpenAsync());

        // an agent who leaves owes nothing
        await SetAgentAsync(ctx, "newbie", a => a.Status = AgentStatus.Terminated);
        Assert.Equal(0, await svc.GetOpenCountAsync(me));
        Assert.False(await MissingAsync(bureau));
    }

    [Fact]
    public async Task A_repealed_or_trashed_paragraph_and_a_trashed_section_are_owed_by_nobody()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var section = await SeedSectionAsync(ctx);
        var (svc, _, reported) = Build(ctx);
        var first = await svc.CreateParagraphAsync(section, Input("Eins"), Boss());
        var second = await svc.CreateParagraphAsync(section, Input("Zwei"), Boss());
        var third = await svc.CreateParagraphAsync(section, Input("Drei"), Boss());
        Assert.Equal(3, await svc.GetOpenCountAsync(Plain("a")));
        reported.Clear();

        await svc.RepealParagraphAsync(first, "überholt", Boss());
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        await svc.DeleteParagraphAsync(second, Boss());
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));

        Assert.Equal(third, Assert.Single(await svc.GetOpenForMeAsync(Plain("a"))).ParagraphId);
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(await RegulationDebt.Audience(db).Where(r => r.ParagraphId == first || r.ParagraphId == second)
                .ToListAsync());
        }

        await svc.RestoreAsync(second, Boss());
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));

        // a section in the trash takes its paragraphs out of every count
        await using (var db = ctx.NewContext())
        {
            var row = await db.RegulationSections.SingleAsync(s => s.Id == section);
            row.IsDeleted = true;
            row.DeletedAt = Stamp;
            await db.SaveChangesAsync();
        }
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Empty(await svc.GetOpenForMeAsync(Plain("a")));
        Assert.Equal(0, (await svc.GetDocumentAsync(Plain("a"))).OpenCount);
    }

    [Fact]
    public async Task The_open_list_is_oldest_first_and_carries_the_publication_of_the_asked_version()
    {
        using var ctx = new SqliteTestContext();
        var asked = Stamp.AddDays(2);
        var young = Stamp.AddDays(5);
        await using (var seed = ctx.NewContext())
        {
            seed.Users.Add(Seed.Agent("a", Rank.Trainee));
            seed.RegulationSections.AddRange(SectionRow("s1", 10, "Erster"), SectionRow("s2", 20, "Zweiter"));
            // p1 asked in its second version, p2 only now, in its first
            seed.RegulationParagraphs.AddRange(
                ParagraphRow("p1", "s2", "Alt", configure: p => { p.CurrentVersion = 3; p.AcknowledgeFromVersion = 2; }),
                ParagraphRow("p2", "s1", "Jung"));
            seed.RegulationVersions.AddRange(
                VersionRow("p1", 1, Stamp),
                VersionRow("p1", 2, asked),
                VersionRow("p1", 3, Stamp.AddDays(9)),
                VersionRow("p2", 1, young));
            // the first version of p1 is too old to count
            seed.RegulationAcknowledgments.Add(AckRow("p1", "a", 1));
            await seed.SaveChangesAsync();
        }
        var (svc, _, _) = Build(ctx);

        var open = await svc.GetOpenForMeAsync(Plain("a"));

        Assert.Equal(["DVO Abschnitt 2 · Alt", "DVO Abschnitt 1 · Jung"], open.Select(o => o.Label));
        Assert.Equal([2, 1], open.Select(o => o.Version));
        Assert.Equal([asked, young], open.Select(o => o.Since));
        Assert.Equal(["Alt", "Jung"], open.Select(o => o.Heading));
        Assert.Equal(["s2", "s1"], open.Select(o => o.SectionId));
        Assert.Equal(2, await svc.GetOpenCountAsync(Plain("a")));

        // order follows the dates
        await using (var db = ctx.NewContext())
        {
            (await db.RegulationVersions.SingleAsync(v => v.ParagraphId == "p2")).PublishedAt = Stamp.AddDays(1);
            await db.SaveChangesAsync();
        }
        Assert.Equal(["p2", "p1"], (await svc.GetOpenForMeAsync(Plain("a"))).Select(o => o.ParagraphId));
    }
}
