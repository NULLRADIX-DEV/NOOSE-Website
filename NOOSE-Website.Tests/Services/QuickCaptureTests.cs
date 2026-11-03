using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Factions;
using NOOSE_Website.Data.Entities.Groups;
using NOOSE_Website.Data.Entities.Handbook;
using NOOSE_Website.Data.Entities.Parties;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Radio;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Search;

namespace NOOSE_Website.Tests.Services;

/// <summary>What the quick-capture dialog may write to. The dialog above it has no test, so this carries the rules.</summary>
public class QuickCaptureTests
{
    // any well-formed GUID; MentionParser only builds a token it could parse back
    private const string AnyGuid = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private static QuickHit Hit(string category, string id = "id-1")
        => new(category, id, "Irgendein Name", "FIB-P-2026-0001");

    // ==================== the note targets ====================

    [Fact]
    public void EveryNoteTargetIsOfferedByTheQuickSearch()
    {
        foreach (var type in QuickCapture.CommentTargets)
        {
            // the picker is fed by QuickSearchAsync, which answers for this trait and nothing else - without it
            // the entry would sit in the list and never produce a single candidate
            Assert.True(SearchCatalog.Has(type, SearchTraits.Quick),
                $"'{type}' ist ein Vermerk-Ziel, trägt aber SearchTraits.Quick nicht — die Schnellsuche findet es nie.");
        }
    }

    [Fact]
    public void EveryNoteTargetLeadsToAPageOfItsOwn()
    {
        foreach (var type in QuickCapture.CommentTargets)
        {
            // CommentService pings every mention in the saved note and hands SearchNavigation.For as the link;
            // a type without a route would send that notification to nowhere
            Assert.True(SearchNavigation.For(type, "irgendeine-id") is not null,
                $"'{type}' hat keine eigene Route — die Erwähnungs-Benachrichtigung des Vermerks zeigt ins Leere.");
        }
    }

    // ==================== the activity targets ====================

    [Fact]
    public void ActivityTargetsAreExactlyTheTwoTheLinkTableAccepts()
    {
        // AktivitaetVerknuepfungen holds a faction or a person group and nothing else, enforced server-side
        Assert.True(QuickCapture.ActivityTargets.SetEquals(new[] { nameof(Faction), nameof(PersonGroup) }),
            "Die Aktivität lässt sich nur mit Fraktion und Personengruppe verknüpfen — die Tabelle nimmt nichts anderes an.");
    }

    [Fact]
    public void EveryActivityTargetIsOfferedByTheQuickSearchAndLeadsSomewhere()
    {
        foreach (var type in QuickCapture.ActivityTargets)
        {
            Assert.True(SearchCatalog.Has(type, SearchTraits.Quick),
                $"'{type}' ist ein Aktivitäts-Ziel, trägt aber SearchTraits.Quick nicht — die Schnellsuche findet es nie.");
            Assert.True(SearchNavigation.For(type, "irgendeine-id") is not null,
                $"'{type}' hat keine eigene Route — der Hinweis nach dem Speichern könnte die Akte nicht verlinken.");
        }
    }

    // ==================== Only ====================

    [Fact]
    public void OnlyKeepsWhatThePickerAccepts()
    {
        var hits = new[] { Hit(nameof(Person)), Hit(nameof(Faction)), Hit(nameof(Party)) };

        Assert.Equal(3, QuickCapture.Only(hits, QuickCapture.CommentTargets, 8).Count);
    }

    [Theory]
    [InlineData(nameof(Document))]        // gated properly, but carries no comment section: the note would be written and invisible for good
    [InlineData(nameof(HandbookArticle))] // falls through the open tail of Visibility, and its hit carries the slug instead of an id
    [InlineData(nameof(GlossaryTerm))]    // same open tail, and the term has no page of its own at all
    [InlineData(nameof(RadioChannel))]    // same open tail: the note would pass no visibility gate whatsoever
    [InlineData(nameof(Agent))]           // the quick search offers the personnel file to everyone, Visibility gives it to leadership
    public void OnlyDropsAQuickCategoryThatIsNoNoteTarget(string category)
    {
        // the category really is offered by the search, so the narrowing here is what keeps it out - not luck
        Assert.True(SearchCatalog.Has(category, SearchTraits.Quick),
            $"'{category}' trägt Quick nicht mehr — dann prüft dieser Fall nichts.");

        Assert.Empty(QuickCapture.Only(new[] { Hit(category) }, QuickCapture.CommentTargets, 8));
    }

    [Fact]
    public void OnlyNarrowsAnActivityPickerFurtherThanANote()
    {
        var hits = new[] { Hit(nameof(Person)), Hit(nameof(Faction)), Hit(nameof(PersonGroup)) };

        Assert.Equal(
            new[] { nameof(Faction), nameof(PersonGroup) },
            QuickCapture.Only(hits, QuickCapture.ActivityTargets, 8).Select(h => h.Category).ToArray());
    }

    [Fact]
    public void OnlyCapsAtMaxAndKeepsTheOrderItWasGiven()
    {
        // the search ranks before it hands over, so reordering here would put the worse match on top
        var hits = new[] { Hit(nameof(Person), "a"), Hit(nameof(Faction), "b"), Hit(nameof(Party), "c") };

        Assert.Equal(
            new[] { "a", "b" },
            QuickCapture.Only(hits, QuickCapture.CommentTargets, 2).Select(h => h.TargetId).ToArray());
    }

    [Fact]
    public void TheCapCountsWhatSurvivesTheFilter()
    {
        // the quick search fills its slots round-robin across all categories, so the dropped ones arrive mixed in;
        // counting them against the cap would leave the picker half empty
        var hits = new[] { Hit(nameof(Document), "d"), Hit(nameof(Person), "a"), Hit(nameof(Faction), "b") };

        Assert.Equal(
            new[] { "a", "b" },
            QuickCapture.Only(hits, QuickCapture.CommentTargets, 2).Select(h => h.TargetId).ToArray());
    }

    [Fact]
    public void OnlyAnswersWithAnEmptyListWhenThereIsNothingToShow()
    {
        Assert.Empty(QuickCapture.Only(null, QuickCapture.CommentTargets, 8));
        Assert.Empty(QuickCapture.Only(new[] { Hit(nameof(Person)) }, QuickCapture.CommentTargets, 0));
        Assert.Empty(QuickCapture.Only(new[] { Hit(nameof(Person)) }, QuickCapture.CommentTargets, -1));
    }

    // ==================== FetchCount ====================

    [Fact]
    public void FetchCountAsksForMoreTheNarrowerThePickerIs()
    {
        var note = QuickCapture.FetchCount(8, QuickCapture.CommentTargets);
        var activity = QuickCapture.FetchCount(8, QuickCapture.ActivityTargets);

        // the quick search fills its slots round-robin over every quick category and cuts at the number asked
        // for, so a picker taking two categories of fourteen would see two rows however many it requested
        Assert.True(note >= 8, $"Der Vermerk-Wähler fragt nur {note} Treffer ab.");
        Assert.True(activity > note,
            $"Der Aktivitäts-Wähler nimmt weniger Kategorien an, fragt aber nicht mehr ab ({activity} zu {note}).");
    }

    [Fact]
    public void FetchCountLeavesAnUnnarrowedPickerAlone()
    {
        var everything = SearchCatalog.Categories
            .Where(c => c.Has(SearchTraits.Quick))
            .Select(c => c.Clr)
            .ToHashSet(StringComparer.Ordinal);

        // nothing is thrown away, so there is nothing to compensate for
        Assert.Equal(8, QuickCapture.FetchCount(8, everything));
    }

    [Fact]
    public void FetchCountStaysWithinItsCap()
    {
        // a single-category picker must not sweep the whole index for one row
        var one = new HashSet<string>(StringComparer.Ordinal) { nameof(Person) };

        Assert.InRange(QuickCapture.FetchCount(8, one), 8, 60);
        Assert.InRange(QuickCapture.FetchCount(50, one), 50, 60);
    }

    [Fact]
    public void FetchCountAsksForNothingWhenNothingIsShown()
    {
        Assert.Equal(0, QuickCapture.FetchCount(0, QuickCapture.CommentTargets));
        Assert.Equal(0, QuickCapture.FetchCount(-1, QuickCapture.CommentTargets));
    }

    // ==================== HoldsImage ====================

    [Fact]
    public void HoldsImageSeesAPastedPicture()
    {
        var text = $"Vor Ort gesehen. {MentionParser.Token(nameof(TextImage), AnyGuid)} Danach war er weg.";

        Assert.True(QuickCapture.HoldsImage(text));
    }

    [Fact]
    public void HoldsImageIgnoresPlainText()
    {
        Assert.False(QuickCapture.HoldsImage("Kein Bild, nur ein Satz."));
        Assert.False(QuickCapture.HoldsImage(string.Empty));
        Assert.False(QuickCapture.HoldsImage(null));
    }

    [Fact]
    public void HoldsImageIgnoresATokenOfAnotherType()
    {
        // a mention is not a picture and hangs on nothing; locking the picker on one would stop an agent from
        // correcting a target he picked by mistake
        Assert.False(QuickCapture.HoldsImage(MentionParser.Token(nameof(Person), AnyGuid)));
    }
}
