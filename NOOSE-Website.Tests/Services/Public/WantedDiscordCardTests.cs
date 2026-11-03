using System.Reflection;
using System.Text.RegularExpressions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Public;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Public;

/// <summary>The Discord card of a public notice: wording, colour, image placement, limits and what it must never carry.</summary>
public class WantedDiscordCardTests
{
    private const string Case = "FIB-FA-2026-0001";
    private static readonly DateTime At = new(2026, 11, 2, 14, 3, 0, DateTimeKind.Utc);

    private static PublicWantedCard Card(
        PublicWantedKind kind = PublicWantedKind.Fahndung,
        string name = "Max Mustermann",
        string? alias = null,
        bool photo = false,
        HazardLevel hazard = HazardLevel.No,
        IReadOnlyList<PublicWantedHint>? hints = null,
        DateTime? published = null)
        => new(Case, kind, name, alias, photo, hazard, published ?? At, hints ?? []);

    private static WantedDiscordEmbed Published(PublicWantedCard card, PublicBounty? bounty = null)
        => WantedDiscordCard.Published(card, bounty);

    private static WantedDiscordEmbed Raised(PublicWantedCard card, decimal total = 250_000m, bool cap = false)
        => WantedDiscordCard.BountyRaised(card, new PublicBounty(total, cap), At);

    // ---- title and link ----

    [Theory]
    [InlineData(PublicWantedKind.Fahndung, "🔎 Fahndung: Max Mustermann")]
    [InlineData(PublicWantedKind.Vermisst, "❓ Vermisst: Max Mustermann")]
    [InlineData(PublicWantedKind.Zeugenaufruf, "📣 Zeugenaufruf: Max Mustermann")]
    [InlineData(PublicWantedKind.Fahrzeug, "🚗 Sachfahndung Fahrzeug: Max Mustermann")]
    [InlineData(PublicWantedKind.Waffe, "🔫 Sachfahndung Waffe: Max Mustermann")]
    public void TheTitle_NamesTheKindAndTheNotice(PublicWantedKind kind, string expected)
        => Assert.Equal(expected, Published(Card(kind)).Title);

    [Fact]
    public void EveryKind_HasATitleWithItsOwnWording()
    {
        var titles = PublicWantedKindDisplay.All.Select(kind => Published(Card(kind)).Title).ToList();

        Assert.Equal(titles.Count, titles.Distinct().Count());
    }

    [Fact]
    public void TheBountyRaiseTitle_IsTheMoneyBagAndTheName()
        => Assert.Equal("💰 Kopfgeld erhöht: Max Mustermann", Raised(Card()).Title);

    [Fact]
    public void TheLink_IsTheRelativePublicProfile()
    {
        // the webhook service owns the base URL; the pure builder never sees it
        Assert.Equal("/gesucht/" + Case, Published(Card()).ProfilePath);
        Assert.Equal("/gesucht/" + Case, Raised(Card()).ProfilePath);
    }

    [Fact]
    public void ALineBreakInTheName_BecomesASpace()
        => Assert.Equal("🔎 Fahndung: Max Mustermann", Published(Card(name: "Max\r\n  Mustermann")).Title);

    // ---- escaping ----

    [Fact]
    public void FreeText_CannotSwitchOnFormattingOrBreakOutOfALink()
    {
        var hints = new[] { new PublicWantedHint("**laut** [x](https://x.test)", "Error") };
        var card = Published(Card(name: "Evil **fett** <@123> #tag", alias: "~~weg~~ `code` _kursiv_", hints: hints));

        Assert.Equal(@"🔎 Fahndung: Evil \*\*fett\*\* \<@123\> \#tag", card.Title);
        Assert.Equal(@"\~\~weg\~\~ \`code\` \_kursiv\_", WantedCardText.Field(card, "Auch bekannt als"));
        // brackets are swapped, not escaped: Discord pairs them before it reads escapes
        Assert.Equal(@"🔴 \*\*laut\*\* ［x］\(https://x.test\)", WantedCardText.Field(card, "Warnhinweise"));
    }

    [Fact]
    public void ALongNameWithManyMarkdownCharacters_IsCutAtAWholeEscape()
    {
        var card = Published(Card(name: new string('*', PublicWantedRules.MaxDisplayName)));

        Assert.True(card.Title.Length <= WantedDiscordCard.MaxTitleChars);
        // never a lone backslash in front of the ellipsis, which would leave the dots escaped
        Assert.Matches(@"^🔎 Fahndung: (\\\*)+…$", card.Title);
    }

    [Fact]
    public void ALongNameWithEmoji_IsNeverCutInHalf()
    {
        var card = Published(Card(name: string.Concat(Enumerable.Repeat("🔥", 200))));

        Assert.True(card.Title.Length <= WantedDiscordCard.MaxTitleChars);
        // a strict encoder throws on half an emoji
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(card.Title);
        Assert.EndsWith("🔥…", card.Title);
    }

    // ---- fields ----

    [Fact]
    public void ThePublishedCard_ShowsTheCaseNumberAndTheHazardLevel()
    {
        var card = Published(Card(hazard: HazardLevel.High));

        Assert.Equal(Case, WantedCardText.Field(card, "Aktenzeichen"));
        Assert.Equal("Hoch", WantedCardText.Field(card, "Gefahrenstufe"));
        Assert.All(card.Fields.Take(2), f => Assert.True(f.Inline));
    }

    [Theory]
    [InlineData(HazardLevel.No, "Keine")]
    [InlineData(HazardLevel.Low, "Niedrig")]
    [InlineData(HazardLevel.Medium, "Mittel")]
    [InlineData(HazardLevel.High, "Hoch")]
    [InlineData(HazardLevel.Critical, "Kritisch")]
    public void TheHazardLevel_IsShownForEveryLevelLikeTheProfileDoes(HazardLevel level, string expected)
        => Assert.Equal(expected, WantedCardText.Field(Published(Card(hazard: level)), "Gefahrenstufe"));

    [Fact]
    public void TheBountyField_ExistsOnlyWhenThereIsMoney()
    {
        Assert.Null(WantedCardText.Field(Published(Card(), null), "Kopfgeld"));
        Assert.Null(WantedCardText.Field(Published(Card(), new PublicBounty(0m, false)), "Kopfgeld"));
        Assert.Equal("250.000 $", WantedCardText.Field(Published(Card(), new PublicBounty(250_000m, false)), "Kopfgeld"));
    }

    [Fact]
    public void ACapBounty_SaysBis()
    {
        Assert.Equal("bis 250.000 $", WantedCardText.Field(Published(Card(), new PublicBounty(250_000m, true)), "Kopfgeld"));
        Assert.Equal("bis 250.000 $", WantedCardText.Field(Raised(Card(), cap: true), "Neues Kopfgeld"));
    }

    [Fact]
    public void TheAliasField_ExistsOnlyWhenThereIsAnAlias()
    {
        Assert.Null(WantedCardText.Field(Published(Card(alias: null)), "Auch bekannt als"));
        Assert.Null(WantedCardText.Field(Published(Card(alias: "   ")), "Auch bekannt als"));
        Assert.Equal("Der Schatten", WantedCardText.Field(Published(Card(alias: " Der Schatten ")), "Auch bekannt als"));
    }

    [Fact]
    public void TheHints_ComeOneLinePerHintInTheGivenOrderWithTheirColourDot()
    {
        PublicWantedHint[] hints =
        [
            new("bewaffnet", "Error"),
            new("fluchtgefährdet", "Warning"),
            new("Zeuge", "Info"),
            new("kooperativ", "Success"),
            new("Sonderfall", "Secondary"),
            new("neutral", "Default"),
        ];

        var value = WantedCardText.Field(Published(Card(hints: hints)), "Warnhinweise");

        Assert.Equal("🔴 bewaffnet\n🟠 fluchtgefährdet\n🟤 Zeuge\n🟢 kooperativ\n🟩 Sonderfall\n⚪ neutral", value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Rot")]
    [InlineData("Magenta")]
    public void AnUnknownColour_GetsTheWhiteDot(string? colour)
        => Assert.StartsWith("⚪ ", WantedCardText.Field(Published(Card(hints: [new("Hinweis", colour!)])), "Warnhinweise"));

    [Fact]
    public void EveryAllowedColour_HasItsOwnDot()
    {
        var dots = WarnhinweisColours.All.Select(c => WarnhinweisColours.Dot(c.Name)).ToList();

        Assert.Equal(WarnhinweisColours.All.Count, dots.Distinct().Count());
        Assert.Equal("⚪", WarnhinweisColours.Dot("nope"));
    }

    [Fact]
    public void NoHints_MeansNoHintField()
        => Assert.Null(WantedCardText.Field(Published(Card(hints: [])), "Warnhinweise"));

    [Fact]
    public void TheRaiseCard_LeadsWithTheNewTotalAndKeepsTheRest()
    {
        var card = Raised(Card(alias: "Der Schatten", hazard: HazardLevel.Medium,
            hints: [new PublicWantedHint("bewaffnet", "Error")]));

        Assert.Equal(["Neues Kopfgeld", "Aktenzeichen", "Gefahrenstufe", "Auch bekannt als", "Warnhinweise"],
            card.Fields.Select(f => f.Name));
        Assert.Equal("250.000 $", WantedCardText.Field(card, "Neues Kopfgeld"));
        Assert.Equal(Case, WantedCardText.Field(card, "Aktenzeichen"));
    }

    // ---- colour ----

    [Theory]
    [InlineData(HazardLevel.Critical, 0xE53935)]
    [InlineData(HazardLevel.High, 0xFB8C00)]
    [InlineData(HazardLevel.Medium, 0xE0C341)]
    [InlineData(HazardLevel.Low, 0x71614A)]
    [InlineData(HazardLevel.No, 0x71614A)]
    public void ThePublishedColour_FollowsTheHazardLevel(HazardLevel level, int expected)
        => Assert.Equal(expected, Published(Card(hazard: level)).Color);

    [Theory]
    [InlineData(HazardLevel.Critical)]
    [InlineData(HazardLevel.No)]
    public void TheRaiseColour_IsGoldWhateverTheHazard(HazardLevel level)
        => Assert.Equal(0xC9A227, Raised(Card(hazard: level)).Color);

    // ---- image ----

    [Fact]
    public void APublishedPhoto_IsTheLargeImage()
    {
        var card = Published(Card(photo: true));

        Assert.Equal($"/gesucht/{Case}/foto", card.ImagePath);
        Assert.Null(card.ThumbnailPath);
    }

    [Fact]
    public void ARaisedBountyPhoto_IsTheThumbnail()
    {
        var card = Raised(Card(photo: true));

        Assert.Equal($"/gesucht/{Case}/foto", card.ThumbnailPath);
        Assert.Null(card.ImagePath);
    }

    [Fact]
    public void WithoutAPhoto_NeitherCardHasAnImage()
    {
        foreach (var card in new[] { Published(Card(photo: false)), Raised(Card(photo: false)) })
        {
            Assert.Null(card.ImagePath);
            Assert.Null(card.ThumbnailPath);
        }
    }

    [Theory]
    [InlineData(PublicWantedKind.Fahrzeug)]
    [InlineData(PublicWantedKind.Waffe)]
    public void AnItemNotice_NeverHasAnImageWhateverTheFlagSays(PublicWantedKind kind)
    {
        foreach (var card in new[] { Published(Card(kind, photo: true)), Raised(Card(kind, photo: true)) })
        {
            Assert.Null(card.ImagePath);
            Assert.Null(card.ThumbnailPath);
        }
    }

    // ---- footer and time ----

    [Fact]
    public void TheFooter_IsTheOffice()
    {
        Assert.Equal("Federal Investigation Bureau", Published(Card()).Footer);
        Assert.Equal("Federal Investigation Bureau", Raised(Card()).Footer);
    }

    [Fact]
    public void ThePublishedTime_IsThePublicationMomentInUtc()
        => Assert.Equal(At, Published(Card()).TimestampUtc);

    [Fact]
    public void AStoredDateThatCameBackUnspecified_IsReadAsUtc()
    {
        var read = Published(Card(published: DateTime.SpecifyKind(At, DateTimeKind.Unspecified))).TimestampUtc;

        Assert.Equal(At, read);
        Assert.Equal(DateTimeKind.Utc, read!.Value.Kind);
    }

    [Fact]
    public void ALocalDate_IsConvertedToUtc()
    {
        var read = Published(Card(published: At.ToLocalTime())).TimestampUtc!.Value;

        Assert.Equal(DateTimeKind.Utc, read.Kind);
        Assert.Equal(At, read);
    }

    [Fact]
    public void WithoutAPublicationMoment_ThereIsNoTimestamp()
    {
        var card = WantedDiscordCard.Published(Card() with { PublishedAt = null }, null);

        Assert.Null(card.TimestampUtc);
    }

    [Fact]
    public void TheRaiseTime_IsTheMomentOfTheRaiseNotThePublication()
    {
        var raisedAt = At.AddDays(3);

        var card = WantedDiscordCard.BountyRaised(Card(), new PublicBounty(5_000m, false), raisedAt);

        Assert.Equal(raisedAt, card.TimestampUtc);
    }

    // ---- limits ----

    [Fact]
    public void ALongHintList_IsCutAtWholeLinesWithACount()
    {
        var hints = Enumerable.Range(1, 40)
            .Select(i => new PublicWantedHint($"Warnhinweis {i:00} " + new string('x', 40), "Error"))
            .ToArray();

        var value = WantedCardText.Field(Published(Card(hints: hints)), "Warnhinweise")!;

        Assert.True(value.Length <= WantedDiscordCard.MaxFieldChars);
        var lines = value.Split('\n');
        Assert.Matches(@"^… und \d+ weitere$", lines[^1]);
        // every line left is a whole hint, none cut in the middle
        Assert.All(lines[..^1], line => Assert.Matches(@"^🔴 Warnhinweis \d\d x{40}$", line));
        var dropped = int.Parse(Regex.Match(lines[^1], @"\d+").Value);
        Assert.Equal(hints.Length, lines.Length - 1 + dropped);
    }

    [Fact]
    public void TheWorstCaseCard_StaysInsideEveryDiscordLimit()
    {
        var hints = Enumerable.Range(0, 100)
            .Select(i => new PublicWantedHint(new string('*', 60), "Error"))
            .ToArray();
        var card = Card(name: new string('*', PublicWantedRules.MaxDisplayName),
            alias: new string('_', PublicWantedRules.MaxAliasText), photo: true, hazard: HazardLevel.Critical,
            hints: hints);

        foreach (var embed in new[] { Published(card, new PublicBounty(99_999_999m, true)), Raised(card) })
        {
            Assert.True(embed.Title.Length <= 256);
            Assert.All(embed.Fields, f =>
            {
                Assert.True(f.Name.Length <= 256);
                Assert.True(f.Value.Length is > 0 and <= 1024);
            });
            Assert.True(embed.Fields.Count <= 25);
            var total = embed.Title.Length + embed.Footer.Length
                + embed.Fields.Sum(f => f.Name.Length + f.Value.Length);
            Assert.True(total <= 6000, $"{total} characters");
        }
    }

    // ---- what the card must never carry ----

    [Fact]
    public void TheRenderedCard_CarriesNeitherTheChargeNorTheInternalCaseNumberNorACodenameNorAScore()
    {
        var detail = new PublicWantedDetail(Case, PublicWantedKind.Fahndung, "Max Mustermann", "Der Schatten", true,
            HazardLevel.Critical, At, "<p>Verdacht auf Waffenhandel</p>", "Vinewood", "Sultan RS", null,
            [new PublicWantedHint("bewaffnet", "Error")]);
        // the way the board derives its cards from a detail
        var card = new PublicWantedCard(detail.CaseNumber, detail.Kind, detail.DisplayName, detail.AliasText,
            detail.HasPhoto, detail.HazardLevel, detail.PublishedAt, detail.Hints);

        foreach (var embed in new[] { Published(card, new PublicBounty(5_000m, false)), Raised(card, 5_000m) })
        {
            var text = WantedCardText.Of(embed);
            Assert.DoesNotContain("Waffenhandel", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Vinewood", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Sultan", text, StringComparison.Ordinal);
            Assert.DoesNotContain("FIB-P-", text, StringComparison.Ordinal);
            Assert.DoesNotContain("NOOSE-P-", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Falcon", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Score", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("<p>", text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBuilder_TakesOnlyOutwardTypes()
    {
        // the bolt is the parameter type, not the care taken: none of these can hold the charge, a PersonId, the
        // internal case number, a codename or a score
        var offenders = typeof(WantedDiscordCard)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .Select(p => p.ParameterType)
            .Where(t => t != typeof(PublicWantedCard) && t != typeof(PublicBounty) && t != typeof(DateTime))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(offenders.Length == 0, "Die Karte darf nur Nach-außen-Typen sehen: " + string.Join(", ", offenders));
    }

    [Fact]
    public void TheCardRecords_HaveNoPlaceForAnInternalIdentifierOrTheCharge()
    {
        string[] forbidden = ["PersonId", "Codename", "Score", "Charge", "Vorwurf", "AgentId", "Rank"];

        var offenders = new[] { typeof(WantedDiscordEmbed), typeof(WantedDiscordField) }
            .SelectMany(t => t.GetProperties())
            .Where(p => forbidden.Any(f => p.Name.Contains(f, StringComparison.Ordinal)))
            .Select(p => p.Name)
            .ToArray();

        Assert.True(offenders.Length == 0, string.Join(", ", offenders));
    }
}
