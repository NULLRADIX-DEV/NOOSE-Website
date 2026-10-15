using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="MentionService"/> against in-memory SQLite.</summary>
public sealed class MentionServiceTests
{
    // A valid 36-char GUID so MentionParser's regex matches the built token.
    private const string PersonId = "11111111-1111-1111-1111-111111111111";

    private static (MentionService svc, ISearchService search) NewService(SqliteTestContext ctx, List<QuickHit>? quickHits = null)
    {
        var search = Substitute.For<ISearchService>();
        // never let the substitute hand back a null list (would NRE inside CandidatesAsync)
        search.QuickSearchAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(quickHits ?? new List<QuickHit>());
        return (new MentionService(ctx.Factory, search), search);
    }

    /// <summary>Real-name access implies leadership, so the two flags cannot be varied independently any more —
    /// the picker takes the principal now, and MayRealNameSee is derived from it.</summary>
    private static ClaimsPrincipal Viewer(bool mayRealName)
        => mayRealName
            ? ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.Director).Build()
            : ClaimsPrincipalBuilder.Agent("viewer").WithRank(Rank.JuniorAgent).Build();

    // ---- CandidatesAsync: what may become a token ---------------------------

    /// <summary>
    /// The picker takes its record candidates from the quick search, but not every palette category can carry a
    /// mention. The token is @{Typ:GUID}; a category whose hit id is not a row id - the handbook article is
    /// addressed by its slug - would write one the parser can never match, and it would be stored raw and
    /// rendered literally in somebody's comment. A category with no arm in RecordsReference resolves to
    /// "(nicht verfügbar)" instead of a name.
    /// </summary>
    [Fact]
    public async Task CandidatesAsync_DropsCategoriesThatCannotCarryAMention()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx,
        [
            new QuickHit("Person", PersonId, "Otto Offen", "NOOSE-P-2026-0001"),
            new QuickHit("HandbookArticle", "fahndung-ausschreiben", "Eine Fahndung ausschreiben", string.Empty),
            new QuickHit("GlossaryTerm", "22222222-2222-2222-2222-222222222222", "Prüffall", string.Empty),
        ]);

        var candidates = await svc.CandidatesAsync("Fahndung", Viewer(mayRealName: false));

        Assert.Contains(candidates, c => c.Type == "Person");
        Assert.DoesNotContain(candidates, c => c.Type == "HandbookArticle");
        Assert.DoesNotContain(candidates, c => c.Type == "GlossaryTerm");
    }

    /// <summary>A palette category that cannot carry a mention has to be one of the known exceptions.</summary>
    /// <remarks>
    /// The mention token is <c>@{Typ:GUID}</c> and only resolves for a type in <see cref="LinkService.ReferenceTypes"/>.
    /// Offering any other category in the @-picker would write a token the parser never matches again, and the
    /// reader would see it raw. The filter in <c>CandidatesAsync</c> is what keeps them out; this list is what
    /// keeps a new category from joining them unnoticed.
    /// </remarks>
    [Fact]
    public void EveryQuickCategoryIsMentionableOrADeliberateException()
    {
        var notMentionable = NOOSE_Website.Services.Search.SearchCatalog.Categories
            .Where(c => c.Has(NOOSE_Website.Services.Search.SearchTraits.Quick))
            .Select(c => c.Clr)
            .Where(clr => !LinkService.ReferenceTypes.Contains(clr, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();

        // handbook article and glossary term are addressed by slug and by id-in-a-query, never as a record;
        // a radio channel has no record page at all - the plan opens on it; a presentation's readers depend on
        // groups and invites, which the mention resolver is never handed
        Assert.Equal(["GlossaryTerm", "HandbookArticle", "Presentation", "RadioChannel"], notMentionable);
    }

    /// <summary>A known type is not enough: the id still has to be one the parser accepts.</summary>
    /// <remarks>
    /// The demo instance seeds its agent with the row id "demo-agent", which sits in a mentionable type and is
    /// not a GUID. Offering it wrote @{Agent:demo-agent} into the comment, where MentionParser never matched it
    /// again and the reader saw the raw token.
    /// </remarks>
    [Fact]
    public async Task CandidatesAsync_DropsAHitWhoseIdIsNotAGuid()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx,
        [
            new QuickHit("Agent", PersonId, "Falcon", "NOOSE-42"),
            new QuickHit("Agent", "demo-agent", "Demo", string.Empty),
        ]);

        var candidates = await svc.CandidatesAsync("a", Viewer(mayRealName: false));

        Assert.Contains(candidates, c => c.Id == PersonId);
        Assert.DoesNotContain(candidates, c => c.Id == "demo-agent");
    }

    // ---- ResolveAsync ------------------------------------------------------

    [Fact]
    public async Task ResolveAsync_NullText_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync(null, isLeadership: false, meId: null);

        Assert.Empty(result);
    }

    [Fact]
    public async Task ResolveAsync_NoTokens_ReturnsSinglePlainSegment()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync("nur reiner Text", isLeadership: false, meId: null);

        var seg = Assert.Single(result);
        Assert.False(seg.IsReference);
        Assert.Equal("nur reiner Text", seg.Text);
    }

    [Fact]
    public async Task ResolveAsync_ResolvesPersonMention_WithSurroundingText()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(PersonId, "Max Mustermann");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);
        var text = "Siehe " + MentionParser.Token("Person", PersonId) + " Ende";

        var result = await svc.ResolveAsync(text, isLeadership: false, meId: null);

        Assert.Equal(3, result.Count);
        Assert.False(result[0].IsReference);
        Assert.Equal("Siehe ", result[0].Text);
        Assert.True(result[1].IsReference);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", result[1].Text);
        Assert.False(result[1].Hidden);
        Assert.NotNull(result[1].Href);
        Assert.Equal("Person", result[1].Type);
        Assert.False(result[2].IsReference);
        Assert.Equal(" Ende", result[2].Text);
    }

    [Fact]
    public async Task ResolveAsync_ClassifiedTarget_NonLeadership_RendersHiddenChip()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person(PersonId, "Max Mustermann", p => p.IsClassified = true));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync(MentionParser.Token("Person", PersonId), isLeadership: false, meId: null);

        var seg = Assert.Single(result);
        Assert.True(seg.IsReference);
        Assert.True(seg.Hidden);
        Assert.Equal("Verschlusssache", seg.Text);
        Assert.Null(seg.Href);
    }

    [Fact]
    public async Task ResolveAsync_ClassifiedTarget_Leadership_ShowsName()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(PersonId, "Max Mustermann", p => p.IsClassified = true);
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync(MentionParser.Token("Person", PersonId), isLeadership: true, meId: null);

        var seg = Assert.Single(result);
        Assert.True(seg.IsReference);
        Assert.False(seg.Hidden);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", seg.Text);
        Assert.NotNull(seg.Href);
    }

    [Fact]
    public async Task ResolveAsync_UnknownTarget_RendersNotAvailableChip()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);

        // token points at a non-seeded id -> unresolved
        var result = await svc.ResolveAsync(MentionParser.Token("Person", PersonId), isLeadership: true, meId: null);

        var seg = Assert.Single(result);
        Assert.True(seg.IsReference);
        Assert.Equal("(nicht verfügbar)", seg.Text);
        Assert.Null(seg.Href);
        Assert.False(seg.Hidden);
    }

    [Fact]
    public async Task ResolveAsync_PartnerScope_UnreleasedTarget_Redacted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // person exists and is not classified, but no PartnerShare grants it to the agency
            db.People.Add(Seed.Person(PersonId, "Max Mustermann"));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync(MentionParser.Token("Person", PersonId),
            isLeadership: false, meId: null, partnerAgency: PartnerAgency.LSPD);

        var seg = Assert.Single(result);
        Assert.True(seg.IsReference);
        // dropped from the map by partner scope -> unavailable chip, no name
        Assert.Equal("(nicht verfügbar)", seg.Text);
        Assert.Null(seg.Href);
    }

    [Fact]
    public async Task ResolveAsync_PartnerScope_ReleasedTarget_Shown()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(PersonId, "Max Mustermann");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.PartnerShares.Add(new PartnerShare
            {
                EntityType = "Person",
                EntityId = PersonId,
                Agency = PartnerAgency.LSPD,
                PartnerAgentId = null, // whole-agency share
            });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.ResolveAsync(MentionParser.Token("Person", PersonId),
            isLeadership: false, meId: null, partnerAgency: PartnerAgency.LSPD);

        var seg = Assert.Single(result);
        Assert.True(seg.IsReference);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", seg.Text);
        Assert.NotNull(seg.Href);
    }

    // ---- ResolveManyAsync --------------------------------------------------

    [Fact]
    public async Task ResolveManyAsync_PreservesOrder_AndResolves()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person(PersonId, "Max Mustermann");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);
        var texts = new string?[]
        {
            "erster Text",
            "Siehe " + MentionParser.Token("Person", PersonId),
            "dritter Text",
        };

        var result = await svc.ResolveManyAsync(texts, isLeadership: false, meId: null);

        Assert.Equal(3, result.Count);
        // [0] plain
        var s0 = Assert.Single(result[0]);
        Assert.False(s0.IsReference);
        Assert.Equal("erster Text", s0.Text);
        // [1] plain + resolved reference
        Assert.Equal(2, result[1].Count);
        Assert.Equal("Siehe ", result[1][0].Text);
        Assert.True(result[1][1].IsReference);
        Assert.Equal($"{person.Name} ({person.CaseNumber})", result[1][1].Text);
        // [2] plain
        var s2 = Assert.Single(result[2]);
        Assert.False(s2.IsReference);
        Assert.Equal("dritter Text", s2.Text);
    }

    [Fact]
    public async Task ResolveManyAsync_NoRefs_ReturnsPlainSegmentsPerText()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);
        var texts = new string?[] { "hallo", null, "welt" };

        var result = await svc.ResolveManyAsync(texts, isLeadership: false, meId: null);

        Assert.Equal(3, result.Count);
        Assert.Equal("hallo", Assert.Single(result[0]).Text);
        // null text collapses to a single empty plain segment
        var mid = Assert.Single(result[1]);
        Assert.False(mid.IsReference);
        Assert.Equal(string.Empty, mid.Text);
        Assert.Equal("welt", Assert.Single(result[2]).Text);
    }

    // ---- CandidatesAsync ---------------------------------------------------

    [Fact]
    public async Task CandidatesAsync_BlankText_ReturnsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = NewService(ctx);

        var result = await svc.CandidatesAsync("   ", Viewer(mayRealName: true));

        Assert.Empty(result);
    }

    [Fact]
    public async Task CandidatesAsync_IncludesSearchRecords()
    {
        using var ctx = new SqliteTestContext();
        // a real row id, not a placeholder: the picker only offers what the parser can read back
        var hits = new List<QuickHit> { new("Person", PersonId, "Max Mustermann", "NOOSE-P-2026-0001") };
        var (svc, _) = NewService(ctx, hits);

        var result = await svc.CandidatesAsync("Max", Viewer(mayRealName: false));

        var hit = Assert.Single(result);
        Assert.Equal("Person", hit.Type);
        Assert.Equal(PersonId, hit.Id);
        Assert.Equal("Max Mustermann", hit.Display);
        Assert.Equal("NOOSE-P-2026-0001", hit.Sub);
    }

    [Fact]
    public async Task CandidatesAsync_ReturnsActiveNonTeamLeadAgents_ByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a-active", status: AgentStatus.Active, configure: a => a.Codename = "Falcon"));
            db.Users.Add(Seed.Agent("a-inactive", status: AgentStatus.Blocked, configure: a => a.Codename = "Falconet"));
            db.Users.Add(Seed.Agent("a-lead", status: AgentStatus.Active, configure: a => { a.Codename = "Falconry"; a.IsTeamLead = true; }));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.CandidatesAsync("Falcon", Viewer(mayRealName: false));

        // only the active, non-team-lead agent survives the filter
        var agents = result.Where(h => h.Type == "Agent").ToList();
        var agent = Assert.Single(agents);
        Assert.Equal("a-active", agent.Id);
        Assert.Equal("Falcon", agent.Display);
        Assert.Null(agent.Sub); // real name withheld when mayRealName is false
    }

    [Fact]
    public async Task CandidatesAsync_RealNameSearch_GatedByMayRealName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a-real", status: AgentStatus.Active, configure: a =>
            {
                a.Codename = "Zephyr";
                a.RealName = "Johnny Cash";
            }));
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        // codename "Zephyr" does not contain "Cash"; without mayRealName the real-name branch is disabled
        var denied = await svc.CandidatesAsync("Cash", Viewer(mayRealName: false));
        Assert.Empty(denied.Where(h => h.Type == "Agent"));

        var allowed = await svc.CandidatesAsync("Cash", Viewer(mayRealName: true));
        var agent = Assert.Single(allowed.Where(h => h.Type == "Agent"));
        Assert.Equal("a-real", agent.Id);
        Assert.Equal("Zephyr", agent.Display);
        Assert.Equal("Johnny Cash", agent.Sub);
    }

    [Fact]
    public async Task CandidatesAsync_IncludesSources_ByTitle_WithVisibleParent()
    {
        using var ctx = new SqliteTestContext();
        var person = Seed.Person("pid2", "Max Mustermann");
        using (var db = ctx.NewContext())
        {
            db.People.Add(person);
            db.Sources.Add(new Source
            {
                Id = "src1",
                EntityType = "Person",
                EntityId = "pid2",
                Title = "Wichtiges Beweisstück",
            });
            db.SaveChanges();
        }
        var (svc, _) = NewService(ctx);

        var result = await svc.CandidatesAsync("Beweis", Viewer(mayRealName: false));

        var source = Assert.Single(result.Where(h => h.Type == "Source"));
        Assert.Equal("src1", source.Id);
        Assert.Equal("Wichtiges Beweisstück", source.Display);
        // Sub carries the parent record's display
        Assert.Equal($"{person.Name} ({person.CaseNumber})", source.Sub);
    }
}
