using NOOSE_Website.Infrastructure;

namespace NOOSE_Website.Tests.Services;

/// <summary>The sweep table that rewrites stored NOOSE wording: grammar, order and what it must leave alone.</summary>
public class BrandSweepTests
{
    [Theory]
    // articles of the agency itself
    [InlineData("Die NOOSE ermittelt.", "Das FIB ermittelt.")]
    [InlineData("Wir arbeiten für die NOOSE.", "Wir arbeiten für das FIB.")]
    [InlineData("Die Leiterin der NOOSE meldet sich.", "Die Leiterin des FIB meldet sich.")]
    [InlineData("Einsatzkräfte der NOOSE haben das Gebäude durchsucht.", "Einsatzkräfte des FIB haben das Gebäude durchsucht.")]
    // compounds keep their article
    [InlineData("Die NOOSE-Führung entscheidet.", "Die FIB-Führung entscheidet.")]
    [InlineData("Der NOOSE-Agent meldet sich.", "Der FIB-Agent meldet sich.")]
    [InlineData("Auftrag der NOOSE-Einheit", "Auftrag der FIB-Einheit")]
    [InlineData("Seit der NOOSE-Gründung", "Seit der FIB-Gründung")]
    [InlineData("bei der NOOSE-Zentrale", "bei der FIB-Zentrale")]
    [InlineData("ein NOOSE-Agent und eine NOOSE-Akte", "ein FIB-Agent und eine FIB-Akte")]
    // contracted prepositions
    [InlineData("Er arbeitet bei der NOOSE.", "Er arbeitet beim FIB.")]
    [InlineData("Ein Brief von der NOOSE.", "Ein Brief vom FIB.")]
    [InlineData("Zugang zur NOOSE.", "Zugang zum FIB.")]
    [InlineData("Der Weg zu der NOOSE.", "Der Weg zum FIB.")]
    [InlineData("Sie dient in der NOOSE.", "Sie dient im FIB.")]
    [InlineData("Er ist an der NOOSE beteiligt.", "Er ist am FIB beteiligt.")]
    // dative prepositions
    [InlineData("Zusammenarbeit mit der NOOSE.", "Zusammenarbeit mit dem FIB.")]
    [InlineData("Ein Agent aus der NOOSE.", "Ein Agent aus dem FIB.")]
    [InlineData("Warnung vor der NOOSE.", "Warnung vor dem FIB.")]
    // sentence starts
    [InlineData("Bei der NOOSE gilt das nicht.", "Beim FIB gilt das nicht.")]
    [InlineData("In der NOOSE gilt das nicht.", "Im FIB gilt das nicht.")]
    [InlineData("Zur NOOSE gehört das nicht.", "Zum FIB gehört das nicht.")]
    [InlineData("Nach der NOOSE folgt das LSPD.", "Nach dem FIB folgt das LSPD.")]
    [InlineData("Neben der NOOSE ermittelt das LSPD.", "Neben dem FIB ermittelt das LSPD.")]
    [InlineData("Mit der NOOSE ist es abgestimmt.", "Mit dem FIB ist es abgestimmt.")]
    // a swap cannot see word boundaries: words that merely end like a preposition stay whole
    [InlineData("Die Agentin der NOOSE", "Die Agentin des FIB")]
    [InlineData("Der Mann der NOOSE", "Der Mann des FIB")]
    [InlineData("Das Haus der NOOSE", "Das Haus des FIB")]
    [InlineData("Der Verlauf der NOOSE", "Der Verlauf des FIB")]
    [InlineData("LSPD oder NOOSE", "LSPD oder FIB")]
    [InlineData("jeder NOOSE-Agent", "jeder FIB-Agent")]
    // the assistant keeps its own gender
    [InlineData("die NOOSEI antwortet", "die FIBI antwortet")]
    [InlineData("Die NOOSEI-Analyse", "Die FIBI-Analyse")]
    [InlineData("Frage an NOOSEI", "Frage an FIBI")]
    // names, abbreviations, addresses
    [InlineData("National Office of Security Enforcement", "Federal Investigation Bureau")]
    [InlineData("Das National Office of Security Enforcement verleiht", "Das Federal Investigation Bureau verleiht")]
    [InlineData("N.O.O.S.E. · National Office of Security Enforcement", "F.I.B. · Federal Investigation Bureau")]
    [InlineData("Bewerbung bei NOOSE", "Bewerbung bei FIB")]
    [InlineData("https://noose.info/gesucht/NOOSE-FA-2026-0001", "https://fibhq.com/gesucht/FIB-FA-2026-0001")]
    [InlineData("demo.noose.info", "demo.fibhq.com")]
    [InlineData("Aktenzeichen NOOSE-P-2026-0001 und NOOSE-PM-2026-0002", "Aktenzeichen FIB-P-2026-0001 und FIB-PM-2026-0002")]
    // what stays: lowercase classes inside stored HTML, mixed case, text without the brand
    [InlineData("<p class=\"noose-kasten-info\">NOOSE</p>", "<p class=\"noose-kasten-info\">FIB</p>")]
    [InlineData("Noose und noose", "Noose und noose")]
    [InlineData("Das LSPD ermittelt.", "Das LSPD ermittelt.")]
    public void Apply_RewritesSentencesWithTheRightGrammar(string input, string expected)
        => Assert.Equal(expected, BrandSweep.Apply(input));

    [Theory]
    [InlineData("Die NOOSE ermittelt bei der NOOSE-Zentrale gegen die NOOSEI.")]
    [InlineData("Die Leiterin der NOOSE, LSPD oder NOOSE, https://noose.info/NOOSE-FA-2026-0001")]
    [InlineData("Mit der NOOSE in der NOOSE vor der NOOSE")]
    [InlineData("in der Government Facility des National Office of Security Enforcement 5020, Los Santos, San Andreas")]
    public void Apply_IsIdempotent_SoARerunFindsNothingToDo(string input)
    {
        var once = BrandSweep.Apply(input);

        Assert.Equal(once, BrandSweep.Apply(once));
        Assert.DoesNotContain("NOOSE", once, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_RewritesTheHeadquartersSentenceOfTheInvitationLetters()
        => Assert.Equal("Die Anhörung wird am DATUM um UHRZEIT Uhr im FIB-Tower, Los Santos, San Andreas stattfinden.",
            BrandSweep.Apply("Die Anhörung wird am DATUM um UHRZEIT Uhr in der Government Facility des "
                + "National Office of Security Enforcement 5020, Los Santos, San Andreas stattfinden."));

    [Fact]
    public void Apply_DropsTheFormerBureauFromTheCareerRequirement()
        => Assert.Equal(
            "Mindestens 3 Monate durchgängige Zugehörigkeit beim Los Santos Police Department "
            + "oder beim Department of Justice mit Erfahrung in einer Position mit Führungsverantwortung",
            BrandSweep.Apply("Mindestens 3 Monate durchgängige Zugehörigkeit beim Los Santos Police Department, "
                + "ehemals Federal Investigation Bureau oder beim Department of Justice mit Erfahrung "
                + "in einer Position mit Führungsverantwortung"));

    [Fact]
    public void Apply_PassesNullAndEmptyThrough()
    {
        Assert.Null(BrandSweep.Apply(null));
        Assert.Equal(string.Empty, BrandSweep.Apply(string.Empty));
    }

    [Fact]
    public void Replacements_RunTheAssistantAndTheCompoundsBeforeTheArticleRules()
    {
        var order = BrandSweep.Replacements.Select(r => r.From).ToList();

        // "die NOOSEI" must not meet "die NOOSE" first, and "die NOOSE-Führung" must keep its article
        Assert.True(order.IndexOf("NOOSEI") < order.IndexOf("die NOOSE"));
        Assert.True(order.IndexOf("die NOOSE-") < order.IndexOf("die NOOSE"));
        Assert.True(order.IndexOf("der NOOSE-") < order.IndexOf(" in der NOOSE"));
        // the contractions run before the bare genitive, the bare genitive before the plain name
        Assert.True(order.IndexOf(" bei der NOOSE") < order.IndexOf(" der NOOSE"));
        Assert.True(order.IndexOf(" der NOOSE") < order.IndexOf("NOOSE"));
        // the long name and the dotted form go before the plain name
        Assert.True(order.IndexOf("National Office of Security Enforcement") < order.IndexOf("NOOSE"));
        Assert.True(order.IndexOf("NOOSEI") < order.IndexOf("NOOSE"));
    }

    [Fact]
    public void Replacements_AreUniqueAndNeverEmpty()
    {
        var from = BrandSweep.Replacements.Select(r => r.From).ToList();

        Assert.All(from, f => Assert.NotEmpty(f));
        Assert.Equal(from.Count, from.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Replacements_CarryNothingTheSqlGeneratorWouldHaveToEscape()
        => Assert.All(BrandSweep.Replacements, r => Assert.DoesNotMatch(@"['\\]", r.From + r.To));

    [Fact]
    public void Replacements_NeverProduceTheBrandTheyRemove()
        => Assert.All(BrandSweep.Replacements, r => Assert.DoesNotContain("NOOSE", r.To, StringComparison.Ordinal));
}
