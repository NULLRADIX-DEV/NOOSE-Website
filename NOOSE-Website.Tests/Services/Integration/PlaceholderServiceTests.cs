using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Operations;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="PlaceholderService"/> against in-memory SQLite.</summary>
public sealed class PlaceholderServiceTests
{
    private static PlaceholderService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // Director => IsLeadership => MayClassifiedRead => sees classified records + all taskforces.
    private static ClaimsPrincipal Leader(string codename = "Falke")
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename(codename).Build();

    // Trainee: not leadership, not admin => cannot read classified records.
    private static ClaimsPrincipal Regular(string codename = "Spatz")
        => ClaimsPrincipalBuilder.Agent("reg").WithRank(Rank.Trainee).WithCodename(codename).Build();

    private const string AllTokens =
        "<p>{{Name}}|{{Aktenzeichen}}|{{Datum}}|{{Uhrzeit}}|{{Agent}}|{{Dienstgrad}}</p>";

    // ---- AvailablePlaceholder ---------------------------------------------

    [Fact]
    public void AvailablePlaceholder_ExposesTheSupportedTokens()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var tokens = svc.AvailablePlaceholder.Select(t => t.Token).ToArray();

        Assert.Equal(6, svc.AvailablePlaceholder.Count);
        Assert.Contains("{{Name}}", tokens);
        Assert.Contains("{{Aktenzeichen}}", tokens);
        Assert.Contains("{{Datum}}", tokens);
        Assert.Contains("{{Uhrzeit}}", tokens);
        Assert.Contains("{{Agent}}", tokens);
        Assert.Contains("{{Dienstgrad}}", tokens);
        Assert.All(svc.AvailablePlaceholder, t => Assert.False(string.IsNullOrWhiteSpace(t.Description)));
    }

    // ---- ApplyAsync: trivial inputs ---------------------------------------

    [Fact]
    public async Task ApplyAsync_EmptyHtml_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync(string.Empty, null, null, Leader());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ApplyAsync_NullHtml_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync(null!, null, null, Leader());

        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public async Task ApplyAsync_NoRecordContext_ReplacesDateAndActorTokens_LeavesRecordTokensEmpty()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).WithCodename("Ghost").Build();

        var result = await svc.ApplyAsync(AllTokens, null, null, actor);
        var today = DateTime.Now.ToString("dd.MM.yyyy");

        // {{Name}} and {{Aktenzeichen}} resolve to empty when no record is bound.
        Assert.StartsWith("<p>||", result);
        Assert.Contains(today, result);
        Assert.Contains("|Ghost|Director</p>", result);
        // no leftover tokens for the known placeholders
        Assert.DoesNotContain("{{Datum}}", result);
        Assert.DoesNotContain("{{Uhrzeit}}", result);
    }

    /// <summary>The token is expanded into stored text and leaves the house with it, so the division never shows in it.</summary>
    [Theory]
    [InlineData(Rank.SpecialAgent, "Special Agent")]
    [InlineData(Rank.SeniorSpecialAgent, "Senior Special Agent")]
    [InlineData(Rank.SpecialAgentInCharge, "Special Agent in Charge")]
    [InlineData(Rank.SectionChief, "Section Chief")]
    public async Task ApplyAsync_RankToken_IsTheNeutralTitleEvenForAnHrtMember(Rank rank, string expected)
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("hrt").WithRank(rank).AsDivision(Division.Hrt).AsTru()
            .WithCodename("Ghost").Build();

        var html = await svc.ApplyAsync("{{Dienstgrad}}", null, null, actor);
        var plain = await svc.ApplyPlainAsync("{{Dienstgrad}}", null, null, actor);

        Assert.Equal(expected, html);
        Assert.Equal(expected, plain);
        Assert.NotEqual(RankDisplay.HrtTitle(rank), html);
    }

    [Fact]
    public async Task ApplyAsync_MissingCodenameClaim_YieldsEmptyAgentToken()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        // Agent(..) sets no codename claim => GetCodename() is null => empty.
        var actor = ClaimsPrincipalBuilder.Agent("nocode").WithRank(Rank.SpecialAgent).Build();

        var result = await svc.ApplyAsync("[{{Agent}}]", null, null, actor);

        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task ApplyAsync_MissingRankClaim_YieldsEmptyDienstgradToken()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        // no WithRank => GetRank() is null => Dienstgrad empty.
        var actor = ClaimsPrincipalBuilder.Agent("norank").WithCodename("X").Build();

        var result = await svc.ApplyAsync("[{{Dienstgrad}}]", null, null, actor);

        Assert.Equal("[]", result);
    }

    // ---- ApplyAsync: token matching ---------------------------------------

    [Fact]
    public async Task ApplyAsync_UnknownToken_IsLeftUnchanged()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("a {{Foobar}} b", null, null, Leader());

        Assert.Equal("a {{Foobar}} b", result);
    }

    [Fact]
    public async Task ApplyAsync_TokenMatch_IsCaseInsensitiveAndTolerantOfInnerWhitespace()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.SeniorSpecialAgent).WithCodename("Adler").Build();

        // lowercase token + whitespace inside the braces both resolve.
        var result = await svc.ApplyAsync("{{agent}}/{{ Agent }}", null, null, actor);

        Assert.Equal("Adler/Adler", result);
    }

    // ---- ApplyAsync: record resolution (Person) ---------------------------

    [Fact]
    public async Task ApplyAsync_VisiblePerson_ResolvesNameAndCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Tony Prince", p => p.CaseNumber = "FIB-P-2026-0042"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Non-classified record => even a non-leadership viewer sees it.
        var result = await svc.ApplyAsync("{{Name}} :: {{Aktenzeichen}}", nameof(Person), "p1", Regular());

        Assert.Equal("Tony Prince :: FIB-P-2026-0042", result);
    }

    [Fact]
    public async Task ApplyAsync_ClassifiedPerson_NonLeadership_DoesNotResolveName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Person), "p1", Regular());

        // Not visible to a junior agent => name stays empty.
        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task ApplyAsync_ClassifiedPerson_Leadership_ResolvesName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheim", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Person), "p1", Leader());

        Assert.Equal("[Geheim]", result);
    }

    [Fact]
    public async Task ApplyAsync_HtmlEncodesResolvedValues()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "<b>Tom & Jerry</b>"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}", nameof(Person), "p1", Leader());

        Assert.Equal("&lt;b&gt;Tom &amp; Jerry&lt;/b&gt;", result);
    }

    [Fact]
    public async Task ApplyAsync_EntityTypeWithoutId_SkipsRecordResolution()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Nicht Aufgeloest"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // entityId null => the record branch is skipped entirely.
        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Person), null, Leader());

        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task ApplyAsync_UnknownEntityType_ResolvesEmptyName()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Unknown type is "visible" by default but has no name mapping => empty.
        var result = await svc.ApplyAsync("[{{Name}}]", "Bogus", "x1", Leader());

        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task ApplyAsync_MissingPerson_ResolvesEmptyName()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // Record not found => not visible => empty.
        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Person), "ghost", Leader());

        Assert.Equal("[]", result);
    }

    // ---- ApplyAsync: other record types -----------------------------------

    [Fact]
    public async Task ApplyAsync_ResolvesFaction()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("f1", "Vagos", f => f.CaseNumber = "FIB-F-2026-0007"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Faction), "f1", Regular());

        Assert.Equal("Vagos|FIB-F-2026-0007", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesPersonGroup()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.PersonGroups.Add(new PersonGroup { Id = "g1", Name = "Zelle Nord", CaseNumber = "FIB-G-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(PersonGroup), "g1", Regular());

        Assert.Equal("Zelle Nord|FIB-G-2026-0001", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesParty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Parties.Add(new Party { Id = "pt1", Name = "Volkspartei", CaseNumber = "FIB-PT-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Party), "pt1", Regular());

        Assert.Equal("Volkspartei|FIB-PT-2026-0001", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesOperation_UsingTitleAsName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Operations.Add(new Operation { Id = "o1", Title = "Nachtfalke", CaseNumber = "FIB-OP-2026-0003" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Operation), "o1", Regular());

        Assert.Equal("Nachtfalke|FIB-OP-2026-0003", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesCase_UsingTitleAsName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Cases.Add(Seed.Case("c1", "Ermittlung Alpha", c => c.CaseNumber = "FIB-V-2026-0005"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Case), "c1", Regular());

        Assert.Equal("Ermittlung Alpha|FIB-V-2026-0005", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesJob_UsingTitleAsName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job { Id = "j1", Title = "Aktenpflege", CaseNumber = "FIB-A-2026-0002" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Jobs are always visible if they exist; a junior agent resolves it.
        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Job), "j1", Regular());

        Assert.Equal("Aktenpflege|FIB-A-2026-0002", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesTaskforce_ForLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "t1", Name = "TF Sturm", CaseNumber = "FIB-TF-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Leadership may see all taskforces without membership.
        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Taskforce), "t1", Leader());

        Assert.Equal("TF Sturm|FIB-TF-2026-0001", result);
    }

    [Fact]
    public async Task ApplyAsync_ResolvesAgent_UsingCodename_AndNoCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => { a.Codename = "Wolf"; a.RealName = "Klaus Klarname"; }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyAsync("{{Name}}|{{Aktenzeichen}}", nameof(Agent), "a1", Leader());

        // Codename, never the real name; agents carry no case number.
        Assert.Equal("Wolf|", result);
    }

    [Fact]
    public async Task ApplyAsync_Agent_NonLeadership_DoesNotResolve()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Wolf"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // Agent records are leadership-only => a junior agent resolves nothing.
        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Agent), "a1", Regular());

        Assert.Equal("[]", result);
    }

    [Fact]
    public async Task ApplyAsync_Taskforce_NonMemberNonLeadership_DoesNotResolve()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(new Taskforce { Id = "t1", Name = "TF Sturm", CaseNumber = "FIB-TF-2026-0001" });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // A junior agent who is not a member cannot see the taskforce => empty.
        var result = await svc.ApplyAsync("[{{Name}}]", nameof(Taskforce), "t1", Regular());

        Assert.Equal("[]", result);
    }

    // ---- ApplyPlainAsync: the plain-text path ------------------------------

    private const string PlainTokens =
        "{{Name}}|{{Aktenzeichen}}|{{Datum}}|{{Uhrzeit}}|{{Agent}}|{{Dienstgrad}}";

    [Fact]
    public async Task ApplyPlainAsync_KeepsUmlautsAndMarkupCharacters_WhereTheHtmlPathEncodesThem()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Müller & <Söhne>"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var plain = await svc.ApplyPlainAsync("{{Name}}", nameof(Person), "p1", Leader());
        var html = await svc.ApplyAsync("{{Name}}", nameof(Person), "p1", Leader());

        // a comment field shows what it is handed, so an encoded umlaut would arrive as the entity itself
        Assert.Equal("Müller & <Söhne>", plain);
        // the same value on the HTML path, encoded — the two paths must not be interchangeable
        Assert.NotEqual(plain, html);
        Assert.DoesNotContain("ü", html);
        Assert.DoesNotContain("<S", html);
        Assert.Contains("&amp;", html);
    }

    [Fact]
    public async Task ApplyPlainAsync_NoRecordContext_ReplacesActorTokens_ButLeavesRecordTokensRaw()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var actor = ClaimsPrincipalBuilder.Agent("me").WithRank(Rank.Director).WithCodename("Ghost").Build();

        var plain = await svc.ApplyPlainAsync(PlainTokens, null, null, actor);
        var html = await svc.ApplyAsync(PlainTokens, null, null, actor);

        // nothing answered for the record tokens, so they stay standing: the agent sees that something is missing
        Assert.StartsWith("{{Name}}|{{Aktenzeichen}}|", plain);
        Assert.Contains(DateTime.Now.ToString("dd.MM.yyyy"), plain);
        Assert.EndsWith("|Ghost|Director", plain);
        // the HTML path blanks them instead, which reads as "nothing belonged here"
        Assert.StartsWith("||", html);
    }

    [Fact]
    public async Task ApplyPlainAsync_EntityTypeWithoutId_LeavesRecordTokensRaw()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Nicht Aufgeloest"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // the record branch is skipped entirely, which is still "no record answered"
        var result = await svc.ApplyPlainAsync("[{{Name}}]", nameof(Person), null, Leader());

        Assert.Equal("[{{Name}}]", result);
    }

    [Fact]
    public async Task ApplyPlainAsync_VisibleRecord_ResolvesNameAndCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Tony Prince", p => p.CaseNumber = "FIB-P-2026-0042"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyPlainAsync("{{Name}} :: {{Aktenzeichen}}", nameof(Person), "p1", Regular());

        Assert.Equal("Tony Prince :: FIB-P-2026-0042", result);
    }

    [Fact]
    public async Task ApplyPlainAsync_ClassifiedPerson_NonLeadership_LeavesTokensRaw_AndRevealsNothing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheimer Name", p =>
            {
                p.IsClassified = true;
                p.CaseNumber = "FIB-P-2026-0099";
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyPlainAsync("[{{Name}}] [{{Aktenzeichen}}]", nameof(Person), "p1", Regular());

        // not visible is handled like no record at all, and nothing about it may reach the field
        Assert.Equal("[{{Name}}] [{{Aktenzeichen}}]", result);
        Assert.DoesNotContain("Geheimer Name", result);
        Assert.DoesNotContain("FIB-P-2026-0099", result);
    }

    [Fact]
    public async Task ApplyPlainAsync_ClassifiedPerson_Leadership_ResolvesName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("p1", "Geheimer Name", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.ApplyPlainAsync("[{{Name}}]", nameof(Person), "p1", Leader());

        Assert.Equal("[Geheimer Name]", result);
    }

    [Fact]
    public async Task ApplyPlainAsync_CarrierTypeItCannotResolve_LeavesTheTokensRaw()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // a comment hangs on plenty of types this service never learned - a meeting, an appointment, an
        // evidence item. Visibility answers "visible" for them, so only the resolver can say "I do not know
        // this one"; blanking the tokens there would be the silent hole the plain path exists to avoid.
        var result = await svc.ApplyPlainAsync("zu {{Name}} ({{Aktenzeichen}})", "RadioChannel", "irgendeine-id", Leader());

        Assert.Equal("zu {{Name}} ({{Aktenzeichen}})", result);
    }

    [Fact]
    public async Task ApplyAsync_CarrierTypeItCannotResolve_StillBlanksTheTokens()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // the HTML path is unchanged by that: a template renders what it can and leaves no token behind
        var result = await svc.ApplyAsync("zu {{Name}} ({{Aktenzeichen}})", "RadioChannel", "irgendeine-id", Leader());

        Assert.Equal("zu  ()", result);
    }

    [Fact]
    public async Task ApplyPlainAsync_UnknownToken_IsLeftUnchanged()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        var result = await svc.ApplyPlainAsync("a {{Foobar}} b", null, null, Leader());

        Assert.Equal("a {{Foobar}} b", result);
    }
}
