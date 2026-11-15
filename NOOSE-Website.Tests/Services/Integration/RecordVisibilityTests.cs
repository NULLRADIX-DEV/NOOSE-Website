using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The one read gate of the six classifiable record types, and proof that EF translates it through the
/// marker interface rather than falling back to client evaluation.</summary>
public class RecordVisibilityTests
{
    private static ViewerScope Plain() => new(false, false, "me", null);
    private static ViewerScope Tru() => new(false, false, "me", null, IsTru: true);
    private static ViewerScope Hrb() => new(false, false, "me", null, IsHrb: true);
    private static ViewerScope Leadership() => new(true, true, "lead", null, IsLeadership: true);

    // ---- LevelOf ----

    [Theory]
    [InlineData(false, false, false, DocumentClassification.None)]
    [InlineData(true, false, false, DocumentClassification.Leadership)]
    [InlineData(true, true, false, DocumentClassification.Tru)]
    [InlineData(true, false, true, DocumentClassification.Hrb)]
    public void LevelOf_maps_the_three_flags(bool classified, bool tru, bool hrb, DocumentClassification expected)
    {
        Assert.Equal(expected, RecordVisibility.LevelOf(classified, tru, hrb));
    }

    [Fact]
    public void LevelOf_prefers_tru_when_both_audience_flags_are_set()
    {
        Assert.Equal(DocumentClassification.Tru, RecordVisibility.LevelOf(true, true, true));
    }

    [Fact]
    public void LevelOf_still_restricts_a_row_that_broke_the_setter_invariant()
    {
        // classified=false with an audience flag cannot be produced by SecrecyLevel's setter; if it ever exists,
        // the level must not read as None, or the point-check would show what OnlyVisible hides
        Assert.Equal(DocumentClassification.Tru, RecordVisibility.LevelOf(false, true, false));
        Assert.Equal(DocumentClassification.Hrb, RecordVisibility.LevelOf(false, false, true));
    }

    // ---- in-memory twin agrees with the query ----

    [Fact]
    public void IsVisible_agrees_with_OnlyVisible_for_every_flag_combination()
    {
        using var ctx = new SqliteTestContext();
        var scopes = new[] { Plain(), Tru(), Hrb(), Leadership() };
        // last two are states the setters forbid; the two forms must still agree about them
        var combos = new[]
        {
            (false, false, false), (true, false, false), (true, true, false), (true, false, true),
            (false, true, false), (false, false, true),
        };

        using (var db = ctx.NewContext())
        {
            var index = 0;
            foreach (var (classified, tru, hrb) in combos)
            {
                db.People.Add(Seed.Person($"p{index++}", configure: p =>
                {
                    p.IsClassified = classified;
                    p.IsTRUClassified = tru;
                    p.IsHRBClassified = hrb;
                }));
            }
            db.SaveChanges();
        }

        foreach (var scope in scopes)
        {
            using var db = ctx.NewContext();
            var fromQuery = db.People.OnlyVisible(scope).Select(p => p.Id).OrderBy(x => x).ToList();
            var fromMemory = db.People.AsEnumerable()
                .Where(p => RecordVisibility.IsVisible(scope, p.IsClassified, p.IsTRUClassified, p.IsHRBClassified))
                .Select(p => p.Id).OrderBy(x => x).ToList();

            Assert.Equal(fromMemory, fromQuery);
        }
    }

    // ---- the gate itself ----

    [Fact]
    public void Plain_agent_sees_only_unrestricted_records()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open"));
            db.People.Add(Seed.Person("lead", configure: p => p.IsClassified = true));
            db.People.Add(Seed.Person("tru", configure: p => { p.IsClassified = true; p.IsTRUClassified = true; }));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        Assert.Equal(new[] { "open" }, read.People.OnlyVisible(Plain()).Select(p => p.Id).ToArray());
    }

    [Fact]
    public void Tru_agent_gains_the_tru_tier_but_not_leadership_or_hrb()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open"));
            db.People.Add(Seed.Person("lead", configure: p => p.IsClassified = true));
            db.People.Add(Seed.Person("tru", configure: p => { p.IsClassified = true; p.IsTRUClassified = true; }));
            db.People.Add(Seed.Person("hrb", configure: p => { p.IsClassified = true; p.IsHRBClassified = true; }));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        var ids = read.People.OnlyVisible(Tru()).Select(p => p.Id).OrderBy(x => x).ToArray();

        // this is the arm the person list was missing: a TRU agent could open the record but never find it
        Assert.Equal(new[] { "open", "tru" }, ids);
    }

    [Fact]
    public void Hrb_agent_gains_the_hrb_tier_only()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Factions.Add(Seed.Faction("open"));
            db.Factions.Add(Seed.Faction("tru", configure: f => { f.IsClassified = true; f.IsTRUClassified = true; }));
            db.Factions.Add(Seed.Faction("hrb", configure: f => { f.IsClassified = true; f.IsHRBClassified = true; }));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        var ids = read.Factions.OnlyVisible(Hrb()).Select(f => f.Id).OrderBy(x => x).ToArray();

        Assert.Equal(new[] { "hrb", "open" }, ids);
    }

    [Fact]
    public void Leadership_sees_every_tier()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.People.Add(Seed.Person("open"));
            db.People.Add(Seed.Person("lead", configure: p => p.IsClassified = true));
            db.People.Add(Seed.Person("tru", configure: p => { p.IsClassified = true; p.IsTRUClassified = true; }));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        Assert.Equal(3, read.People.OnlyVisible(Leadership()).Count());
    }

    [Fact]
    public void An_audience_flag_without_the_classified_flag_stays_hidden()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // the setters forbid this state; if a row ever reaches it, the defensive arm hides it
            db.People.Add(Seed.Person("broken", configure: p => p.IsTRUClassified = true));
            db.SaveChanges();
        }

        using var read = ctx.NewContext();
        Assert.Empty(read.People.OnlyVisible(Plain()).ToList());
    }

    // ---- translated, not client-evaluated ----

    [Fact]
    public void The_predicate_reaches_SQL_rather_than_being_evaluated_in_memory()
    {
        using var ctx = new SqliteTestContext();
        using var db = ctx.NewContext();

        var sql = db.People.OnlyVisible(Plain()).ToQueryString();

        // interface member access through the generic constraint must resolve to the mapped columns
        Assert.Contains("Verschlusssache", sql, StringComparison.OrdinalIgnoreCase);
    }

    // ---- every classifiable entity really implements the marker ----

    [Fact]
    public void All_seven_record_types_carry_the_marker_interface()
    {
        var marked = typeof(AppDbContext).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IClassifiableRecord).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(n => n)
            .ToArray();

        Assert.Equal(new[] { "Case", "DrugRoute", "Faction", "Operation", "Party", "Person", "PersonGroup" }, marked);
    }
}
