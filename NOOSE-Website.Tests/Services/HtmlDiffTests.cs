using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The word diff an agent sees before accepting a NOOSEI correction.</summary>
public class HtmlDiffTests
{
    [Fact]
    public void Compare_MarksNothing_WhenNothingChanged()
    {
        var diff = HtmlDiff.Compare("<p>Alles korrekt.</p>", "<p>Alles korrekt.</p>");

        Assert.True(diff.Unchanged);
        Assert.Equal(0, diff.Changes);
        Assert.DoesNotContain("<ins", diff.Html);
        Assert.DoesNotContain("<del", diff.Html);
    }

    [Fact]
    public void Compare_MarksACorrectedWord()
    {
        var diff = HtmlDiff.Compare(
            "<p>Der Verdächtige wurde festgenomen.</p>",
            "<p>Der Verdächtige wurde festgenommen.</p>");

        Assert.Contains("<del class=\"noosei-weg\">festgenomen</del>", diff.Html);
        Assert.Contains("<ins class=\"noosei-neu\">festgenommen</ins>", diff.Html);
        Assert.Contains("Der Verdächtige wurde", diff.Html);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
    }

    [Fact]
    public void Compare_MarksAnInsertion()
    {
        var diff = HtmlDiff.Compare("<p>Er kam an.</p>", "<p>Er kam gestern an.</p>");

        Assert.Contains("<ins", diff.Html);
        Assert.DoesNotContain("<del", diff.Html);
    }

    [Fact]
    public void Compare_MarksADeletion()
    {
        var diff = HtmlDiff.Compare("<p>Er kam gestern an.</p>", "<p>Er kam an.</p>");

        Assert.Contains("<del", diff.Html);
        Assert.DoesNotContain("<ins", diff.Html);
    }

    [Fact]
    public void Compare_EncodesTheText_SoNothingCanBeInjected()
    {
        var diff = HtmlDiff.Compare("<p>Alt</p>", "<p>&lt;script&gt;alert(1)&lt;/script&gt;</p>");

        Assert.DoesNotContain("<script", diff.Html);
    }

    [Fact]
    public void Compare_ReportsAStructureChange_EvenWhenTheWordsAreIdentical()
    {
        var diff = HtmlDiff.Compare(
            "<p>Eins</p><p>Zwei</p><p>Drei</p>",
            "<ul><li>Eins</li><li>Zwei</li><li>Drei</li></ul>");

        // a word diff over text shows nothing here — the fingerprint is what catches it
        Assert.True(diff.StructureChanged);
        Assert.Equal(0, diff.Changes);
    }

    [Fact]
    public void Compare_ReportsNoStructureChange_ForAPlainCorrection()
    {
        var diff = HtmlDiff.Compare("<p>fehlar</p>", "<p>Fehler</p>");

        Assert.False(diff.StructureChanged);
    }

    [Fact]
    public void Compare_MeasuresHowMuchWasTouched()
    {
        var light = HtmlDiff.Compare(
            "<p>Ein sehr langer Satz mit vielen Wörtern und einem fehlar darin.</p>",
            "<p>Ein sehr langer Satz mit vielen Wörtern und einem Fehler darin.</p>");
        var heavy = HtmlDiff.Compare(
            "<p>Ein sehr langer Satz mit vielen Wörtern und einem fehlar darin.</p>",
            "<p>Vollständig anders formulierter Text ohne jede Ähnlichkeit zum Ursprung.</p>");

        Assert.True(light.ChangedRatio < 0.35, $"leichte Korrektur meldete {light.ChangedRatio:P0}");
        Assert.True(heavy.ChangedRatio > 0.35, $"Umformulierung meldete nur {heavy.ChangedRatio:P0}");
    }

    [Fact]
    public void Compare_ShowsARewrite_AsOldThenNew_NotAsWordSalad()
    {
        var diff = HtmlDiff.Compare(
            "<p>Trainees sind im Dienst ausschließlich dazu verpflichtet, Kleidung mit der unverkennbaren Aufschrift oder der "
            + "offiziellen Abkürzung des Federal Investigation Bureau zu tragen.</p>",
            "<p>Trainees sind an der Kleiderordnung der Criminal Investigation Division gebunden.</p>");

        Assert.Equal(
            "<p>Trainees sind <del class=\"noosei-weg\">im Dienst ausschließlich dazu verpflichtet, Kleidung mit der "
            + "unverkennbaren Aufschrift oder der offiziellen Abkürzung des Federal Investigation Bureau zu tragen</del>"
            + "<ins class=\"noosei-neu\">an der Kleiderordnung der Criminal Investigation Division gebunden</ins>.</p>",
            diff.Html);
    }

    [Fact]
    public void Compare_KeepsSeparateCorrections_Apart()
    {
        var diff = HtmlDiff.Compare(
            "<p>Der Verdächtige wurde gestren am Hafen festgenomen.</p>",
            "<p>Der Verdächtige wurde gestern am Hafen festgenommen.</p>");

        Assert.Contains("<ins class=\"noosei-neu\">gestern</ins> am Hafen <del", diff.Html);
    }

    [Fact]
    public void Compare_NeverFoldsAParagraphBreak()
    {
        var diff = HtmlDiff.Compare("<p>alt eins</p><p>alt zwei</p>", "<p>neu eins</p><p>neu zwei</p>");

        Assert.Equal(2, diff.Html.Split("<p>").Length - 1);
    }

    [Fact]
    public void Fingerprint_ListsBlockTagsInOrder()
    {
        Assert.Equal("h2,p,li,li", HtmlDiff.Fingerprint("<h2>T</h2><p>A</p><ul><li>1</li><li>2</li></ul>"));
        Assert.Equal(string.Empty, HtmlDiff.Fingerprint(null));
    }

    [Fact]
    public void Tokenize_SplitsWordsWhitespaceAndPunctuation()
    {
        Assert.Equal(["Hallo", " ", "Welt", "!"], HtmlDiff.Tokenize("Hallo Welt!"));
        Assert.Empty(HtmlDiff.Tokenize(null));
    }

    [Fact]
    public void Compare_SurvivesALargeDocument()
    {
        var words = string.Join(' ', Enumerable.Range(0, 3_000).Select(i => "wort" + i));
        var changed = words.Replace("wort1500", "geaendert");

        var diff = HtmlDiff.Compare($"<p>{words}</p>", $"<p>{changed}</p>");

        Assert.False(diff.Degraded);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
    }
}
