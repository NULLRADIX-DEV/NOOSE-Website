using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>Unit tests for the duplicate name rule (titles, word order, sound-alike, typos).</summary>
public sealed class PersonNameMatchTests
{
    [Theory]
    [InlineData("Dr. Carlos Mendez")]
    [InlineData("Doktor Carlos Mendez")]
    [InlineData("Dr. med. Carlos Mendez")]
    [InlineData("Prof. Carlos Mendez")]
    [InlineData("Professor Dr. Carlos Mendez")]
    [InlineData("Carlos Mendez Jr.")]
    [InlineData("Carlos Mendez Sr")]
    public void Compare_IgnoresTitles(string withTitle)
    {
        Assert.Equal(DuplicateReason.SameName, PersonNameMatch.Compare(withTitle, "Carlos Mendez"));
    }

    [Theory]
    [InlineData("carlos   MENDEZ")]
    [InlineData("Mendez, Carlos")]
    [InlineData("Mendez Carlos")]
    public void Compare_IgnoresCasePunctuationAndOrder(string variant)
    {
        Assert.Equal(DuplicateReason.SameName, PersonNameMatch.Compare(variant, "Carlos Mendez"));
    }

    [Theory]
    [InlineData("Karlos Mendes", "Carlos Mendez")]
    [InlineData("Hans Meyer", "Hans Maier")]
    [InlineData("Hans Müller", "Hans Mueller")]
    public void Compare_SoundAlike(string a, string b)
    {
        Assert.Equal(DuplicateReason.SoundsAlike, PersonNameMatch.Compare(a, b));
    }

    [Theory]
    [InlineData("Carlos Mendex", "Carlos Mendez")]
    [InlineData("Carlos Mendze", "Carlos Mendez")]
    public void Compare_Typo(string a, string b)
    {
        Assert.Equal(DuplicateReason.SpelledAlike, PersonNameMatch.Compare(a, b));
    }

    [Fact]
    public void Compare_WeakestWordDecides()
    {
        // first word only sounds alike, second only has a typo
        Assert.Equal(DuplicateReason.SpelledAlike, PersonNameMatch.Compare("Karlos Mendex", "Carlos Mendez"));
    }

    [Theory]
    [InlineData("Mendez", "Carlos Mendez")]
    [InlineData("Carlos Mendez Garcia", "Carlos Mendez")]
    public void Compare_DifferentWordCount_NoMatch(string a, string b)
    {
        Assert.Null(PersonNameMatch.Compare(a, b));
    }

    [Fact]
    public void Compare_ShortWordsMustBeExact()
    {
        // "Li" and "Lu" share a phonetic code, but two letters are too little to call it the same name
        Assert.Null(PersonNameMatch.Compare("Lu Wang", "Li Wang"));
        Assert.Equal(DuplicateReason.SameName, PersonNameMatch.Compare("li wang", "Li Wang"));
    }

    [Theory]
    [InlineData("Carlos Mendez", "Pablo Escobar")]
    [InlineData("Carlos Mendez", "Carlos Escobar")]
    public void Compare_DifferentNames_NoMatch(string a, string b)
    {
        Assert.Null(PersonNameMatch.Compare(a, b));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("Dr.", "Dr.")]
    [InlineData(null, "Carlos Mendez")]
    public void Compare_NothingLeftToCompare_NoMatch(string? a, string? b)
    {
        Assert.Null(PersonNameMatch.Compare(a, b));
    }

    [Fact]
    public void BlockingKeys_NeverLoseAMatch()
    {
        // every matching pair must share a key between the first word of one side and any word of the other
        var rnd = new Random(11);
        string[] syllables = ["ka", "lo", "men", "dez", "des", "ro", "dri", "guez", "pa", "blo", "es", "co", "bar", "bat",
            "han", "mai", "mey", "er", "an", "ton", "li", "lu", "wang", "jo", "nas", "ti", "nez", "a", "e", "x"];
        string Word() => string.Concat(Enumerable.Range(0, rnd.Next(1, 3)).Select(_ => syllables[rnd.Next(syllables.Length)]));
        var names = Enumerable.Range(0, 600).Select(_ => $"{Word()} {Word()}").ToList();

        var matches = 0;
        foreach (var a in names)
        {
            foreach (var b in names)
            {
                var (pa, pb) = (PersonNameMatch.Prepare(a), PersonNameMatch.Prepare(b));
                if (ReferenceEquals(a, b) || PersonNameMatch.Compare(pa, pb) is null)
                {
                    continue;
                }
                matches++;
                Assert.True(PersonNameMatch.BlockingKeys(pa, firstWordOnly: true).Intersect(PersonNameMatch.BlockingKeys(pb)).Any(),
                    $"\"{a}\" vs \"{b}\" share no key");
            }
        }
        Assert.True(matches > 50, $"only {matches} matches"); // the sample must actually exercise the rule
    }

    [Fact]
    public void Compare_RepeatedWordNeedsItsOwnPartner()
    {
        // both words of "Anna Anna" must pair off; one "Anna" may not serve twice
        Assert.Null(PersonNameMatch.Compare("Anna Anna", "Anna Schmidt"));
    }
}
