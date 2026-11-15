using NetCord;
using NetCord.Rest;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The look of every bot post: what a card says, which buttons it carries and that it always fits.</summary>
public class NotificationCardsTests
{
    private const string BaseUrl = "https://fibhq.com";
    private const string Guid = "0f8fad5b-d9cb-469f-a165-70867728950e";
    private static readonly DateTime Moment = new(2026, 11, 15, 12, 0, 0, DateTimeKind.Utc);

    public static IEnumerable<object[]> Routable => DiscordRouting.RoutableTypes.Select(t => new object[] { t });

    // ---------------------------------------------------------------- helpers

    // webhook notice sentence
    private static string Notice(NotificationType type)
        => DiscordWebhookService.PersonalText(type, BaseUrl, "/x").Split('\n')[0];

    private static IEnumerable<object> Walk(IEnumerable<object> items)
    {
        foreach (var item in items)
        {
            yield return item;
            IEnumerable<object> children = item switch
            {
                ComponentContainerProperties container => container.Components,
                ComponentSectionProperties section => section.Components.Cast<object>().Append(section.Accessory),
                ActionRowProperties row => row.Components,
                _ => [],
            };
            foreach (var child in Walk(children))
            {
                yield return child;
            }
        }
    }

    private static List<object> All(IEnumerable<IMessageComponentProperties> card) => Walk(card).ToList();

    private static List<string> Texts(IEnumerable<IMessageComponentProperties> card)
        => All(card).OfType<TextDisplayProperties>().Select(t => t.Content).ToList();

    private static int TextLength(IEnumerable<IMessageComponentProperties> card) => Texts(card).Sum(t => t.Length);

    private static List<string> CustomIds(IEnumerable<IMessageComponentProperties> card)
        => All(card).OfType<ButtonProperties>().Select(b => b.CustomId).ToList();

    private static List<LinkButtonProperties> Links(IEnumerable<IMessageComponentProperties> card)
        => All(card).OfType<LinkButtonProperties>().ToList();

    private static string? Thumbnail(IEnumerable<IMessageComponentProperties> card)
        => All(card).OfType<ComponentSectionThumbnailProperties>().SingleOrDefault()?.Media.Url;

    private static Color Accent(IReadOnlyList<IMessageComponentProperties> card)
        => ((ComponentContainerProperties)card.Single()).AccentColor!.Value;

    // minus header, footer
    private static List<string> Prose(IEnumerable<IMessageComponentProperties> card)
        => Texts(card).Where(t => !t.StartsWith("### ") && t != DiscordCards.Footer).ToList();

    private static string Body(IEnumerable<IMessageComponentProperties> card) => string.Join("\n", Prose(card));

    private static string Everything(IEnumerable<IMessageComponentProperties> card) => string.Join("\n", Texts(card));

    // ---------------------------------------------------------------- generic cards

    [Theory]
    [MemberData(nameof(Routable))]
    public void Generic_says_no_more_than_the_webhook_text(NotificationType type)
    {
        var card = NotificationCards.Generic(type, BaseUrl, null, null, "");

        Assert.Equal([Notice(type)], Prose(card));
        var (_, emoji, title) = NotificationCards.Style(type);
        Assert.Contains($"### {emoji} {DiscordCards.Escape(title)}", Texts(card));
        Assert.Equal(DiscordCards.Footer, Texts(card)[^1]);
    }

    [Fact]
    public void Headline_shown_when_passed()
    {
        var card = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", "X", "");

        Assert.Equal([Notice(NotificationType.Announcement) + "\n**X**"], Prose(card));
    }

    [Fact]
    public void Headline_is_escaped_single_line_and_capped()
    {
        var raw = "**fett** <@123> @everyone\nzweite Zeile " + new string('a', 400);
        var card = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", raw, "");

        var lines = Body(card).Split('\n');
        Assert.Equal(2, lines.Length);
        var headline = lines[1];
        Assert.StartsWith("**", headline);
        Assert.EndsWith("**", headline);
        // inert markup
        Assert.DoesNotContain("<@", headline);
        Assert.DoesNotContain("**fett**", headline);
        Assert.Contains("\\@everyone", headline);
        var plain = headline[2..^2].Replace("\\", "");
        Assert.True(plain.Length <= 300, $"headline has {plain.Length} chars");
        Assert.EndsWith("…", plain);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_blank_headline_adds_nothing(string? headline)
    {
        var card = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", headline, "");

        Assert.Equal([Notice(NotificationType.Announcement)], Prose(card));
    }

    [Fact]
    public void The_ping_is_the_first_line_and_the_logo_the_thumbnail()
    {
        var card = NotificationCards.Generic(NotificationType.JobAssigned, "https://fibhq.com/", "/aufgaben/abc", null, "<@1> <@2>");

        Assert.Equal("<@1> <@2>", Texts(card)[0]);
        Assert.Equal("https://fibhq.com/FibIcon.png", Thumbnail(card));
        Assert.Equal("/FibIcon.png", DiscordCards.Logo);
    }

    [Fact]
    public void Without_a_ping_the_card_opens_with_its_header()
    {
        var card = NotificationCards.Generic(NotificationType.JobAssigned, BaseUrl, "/aufgaben/abc", null, "");

        Assert.StartsWith("### ", Texts(card)[0]);
    }

    [Fact]
    public void A_card_is_one_container_with_divider_buttons_and_footer()
    {
        var card = NotificationCards.Generic(NotificationType.JobAssigned, BaseUrl, "/aufgaben/abc", null, "");

        var container = Assert.IsType<ComponentContainerProperties>(Assert.Single(card));
        var parts = container.Components.ToList();
        Assert.IsType<ComponentSectionProperties>(parts[0]);
        Assert.IsType<ComponentSeparatorProperties>(parts[^3]);
        Assert.IsType<ActionRowProperties>(parts[^2]);
        Assert.Equal(DiscordCards.Footer, Assert.IsType<TextDisplayProperties>(parts[^1]).Content);
        Assert.DoesNotContain("<t:", Everything(card));
    }

    // ---------------------------------------------------------------- colours and style

    [Fact]
    public void Gold_is_the_decoration_colour()
        => Assert.Equal(0xC9A227, DiscordCards.Gold.RawValue);

    [Theory]
    [InlineData(NotificationType.EmergencyAlarm, nameof(DiscordCards.Danger))]
    [InlineData(NotificationType.AgentTerminated, nameof(DiscordCards.Danger))]
    [InlineData(NotificationType.PublicWantedPublished, nameof(DiscordCards.Orange))]
    [InlineData(NotificationType.PublicWantedBountyRaised, nameof(DiscordCards.Orange))]
    [InlineData(NotificationType.PublicCaptureReported, nameof(DiscordCards.Orange))]
    [InlineData(NotificationType.DecorationAwarded, nameof(DiscordCards.Gold))]
    [InlineData(NotificationType.PersonnelEntry, nameof(DiscordCards.Bronze))]
    [InlineData(NotificationType.Announcement, nameof(DiscordCards.Bronze))]
    [InlineData(NotificationType.SituationReport, nameof(DiscordCards.Bronze))]
    [InlineData(NotificationType.JobDueSoon, nameof(DiscordCards.Warning))]
    [InlineData(NotificationType.MeetingReminder, nameof(DiscordCards.Warning))]
    [InlineData(NotificationType.TestDueReminder, nameof(DiscordCards.Warning))]
    [InlineData(NotificationType.Followup, nameof(DiscordCards.Warning))]
    [InlineData(NotificationType.AcknowledgmentReminder, nameof(DiscordCards.Warning))]
    [InlineData(NotificationType.PublicTicketCreated, nameof(DiscordCards.Sand))]
    [InlineData(NotificationType.PublicTipCreated, nameof(DiscordCards.Sand))]
    [InlineData(NotificationType.PublicPressPublished, nameof(DiscordCards.Sand))]
    [InlineData(NotificationType.JobAssigned, nameof(DiscordCards.Steel))]
    [InlineData(NotificationType.Mention, nameof(DiscordCards.Steel))]
    [InlineData(NotificationType.PingDigest, nameof(DiscordCards.Steel))]
    [InlineData(NotificationType.Recruiting, nameof(DiscordCards.Steel))]
    public void Every_category_has_its_accent(NotificationType type, string colour)
    {
        var expected = (Color)typeof(DiscordCards).GetField(colour)!.GetValue(null)!;

        Assert.Equal(expected, NotificationCards.Style(type).Accent);
        Assert.Equal(expected, Accent(NotificationCards.Generic(type, BaseUrl, null, null, "")));
    }

    [Theory]
    [MemberData(nameof(Routable))]
    public void Style_takes_the_emoji_of_the_notice(NotificationType type)
    {
        var (_, emoji, title) = NotificationCards.Style(type);

        Assert.StartsWith(emoji + " ", Notice(type));
        Assert.False(string.IsNullOrWhiteSpace(title));
    }

    [Fact]
    public void Titles_are_the_display_words_of_the_category()
    {
        Assert.Equal("Aufgabe zugewiesen", NotificationCards.Style(NotificationType.JobAssigned).Title);
        Assert.Equal("📌", NotificationCards.Style(NotificationType.JobAssigned).Emoji);
        Assert.Equal("⚠️", NotificationCards.Style(NotificationType.AgentTerminated).Emoji);
        Assert.Equal("🎖️", NotificationCards.Style(NotificationType.DecorationAwarded).Emoji);
    }

    // ---------------------------------------------------------------- buttons

    public static IEnumerable<object[]> ButtonCases()
    {
        static object[] Case(NotificationType type, string? href, params string[] ids) => [type, href!, false, ids];
        static object[] AckCase(NotificationType type, string? href, params string[] ids) => [type, href!, true, ids];
        static string Details(NotificationType type, string id) => $"fib:nd:{(int)type}:{id}";

        yield return Case(NotificationType.JobAssigned, "/aufgaben/abc", "fib:cjd:abc", Details(NotificationType.JobAssigned, "abc"));
        yield return Case(NotificationType.JobDueSoon, "/aufgaben/abc", "fib:cjd:abc", Details(NotificationType.JobDueSoon, "abc"));
        // acknowledge only where the announcement asks for it
        yield return AckCase(NotificationType.Announcement, "/brett/a1", "fib:cack:a1", Details(NotificationType.Announcement, "a1"));
        yield return Case(NotificationType.Announcement, "/brett/a1", Details(NotificationType.Announcement, "a1"));
        // a reminder exists only for those, so it always acknowledges
        yield return Case(NotificationType.AcknowledgmentReminder, "/brett/a1", "fib:cack:a1");
        yield return AckCase(NotificationType.AcknowledgmentReminder, "/brett/a1", "fib:cack:a1");
        // the flag is no reason for a button where there is nothing to acknowledge
        yield return AckCase(NotificationType.JobAssigned, "/aufgaben/abc", "fib:cjd:abc", Details(NotificationType.JobAssigned, "abc"));
        yield return AckCase(NotificationType.Announcement, "/brett/");
        yield return Case(NotificationType.AppointmentAssigned, "/kalender/k1", "fib:cdates");
        yield return Case(NotificationType.MeetingScheduled, "/besprechungen/b1", "fib:cdates");
        yield return Case(NotificationType.MeetingReminder, "/besprechungen/b1", "fib:cdates");
        yield return Case(NotificationType.InactivityNudge, "/abmeldungen", "fib:habs");
        yield return Case(NotificationType.PublicTipCreated, "/hinweise/t1", "fib:ctip:t1");
        yield return Case(NotificationType.PublicTicketCreated, "/tickets/k1", "fib:ctk:k1");
        yield return Case(NotificationType.Recruiting, "/bewerbungen/b1", "fib:cbw:b1", Details(NotificationType.Recruiting, "b1"));
        yield return Case(NotificationType.Followup, "/personen/p1", Details(NotificationType.Followup, "p1"));
        yield return Case(NotificationType.Mention, "/personen/p1", Details(NotificationType.Mention, "p1"));
        yield return Case(NotificationType.SituationReport, "/lageberichte/l1", Details(NotificationType.SituationReport, "l1"));
        yield return Case(NotificationType.TaskforceChat, "/taskforces/t1", Details(NotificationType.TaskforceChat, "t1"));
        yield return Case(NotificationType.SavedSearchHit, "/suche/s1", Details(NotificationType.SavedSearchHit, "s1"));
        yield return Case(NotificationType.TestDueReminder, "/tests/a1", Details(NotificationType.TestDueReminder, "a1"));
        // list page
        yield return Case(NotificationType.TestAssigned, "/tests");
        yield return Case(NotificationType.PingDigest, "/benachrichtigungen");
        yield return Case(NotificationType.PingDigest, "/benachrichtigungen/x");
        // missing id
        yield return Case(NotificationType.JobAssigned, "/aufgaben");
        yield return Case(NotificationType.JobAssigned, null);
        yield return Case(NotificationType.Announcement, "/brett/");
        // wrong collection
        yield return Case(NotificationType.Recruiting, "/portal/status");
        yield return Case(NotificationType.Recruiting, "/portal/test/abc");
        yield return Case(NotificationType.JobAssigned, "/personen/abc");
        // query, fragment, absolute
        yield return Case(NotificationType.JobAssigned, "/aufgaben/abc?x=1#y", "fib:cjd:abc", Details(NotificationType.JobAssigned, "abc"));
        yield return Case(NotificationType.JobAssigned, "https://fibhq.com/aufgaben/abc", "fib:cjd:abc", Details(NotificationType.JobAssigned, "abc"));
        // unsafe ids
        yield return Case(NotificationType.JobAssigned, "/aufgaben/a:b");
        yield return Case(NotificationType.JobAssigned, "/aufgaben/" + new string('a', 65));
        // link only
        yield return Case(NotificationType.PersonnelEntry, "/personal/p1");
        yield return Case(NotificationType.PublicPressPublished, "/presse/p1");
        yield return Case(NotificationType.PublicCaptureReported, "/hinweise/c1");
        yield return Case(NotificationType.FactionThreatBoard, "/fraktionen");
    }

    [Theory]
    [MemberData(nameof(ButtonCases))]
    public void Buttons_follow_the_table(NotificationType type, string? href, bool acknowledgeable, string[] expectedIds)
    {
        var row = NotificationCards.Buttons(type, href, acknowledgeable: acknowledgeable);

        Assert.NotNull(row);
        Assert.Equal(expectedIds, row!.Components.OfType<ButtonProperties>().Select(b => b.CustomId).ToArray());
        var link = Assert.Single(row.Components.OfType<LinkButtonProperties>());
        Assert.StartsWith(BaseUrl + "/", link.Url);
        Assert.Equal("Öffnen", link.Label);
        // link comes last
        Assert.IsType<LinkButtonProperties>(row.Components.Last());
    }

    [Fact]
    public void Buttons_link_the_record_or_the_category_page()
    {
        Assert.Equal("https://fibhq.com/aufgaben/abc", LinkOf(NotificationCards.Buttons(NotificationType.JobAssigned, "/aufgaben/abc")));
        Assert.Equal("https://example.org/aufgaben/abc",
            LinkOf(NotificationCards.Buttons(NotificationType.JobAssigned, "/aufgaben/abc", "https://example.org/")));
        Assert.Equal("https://fibhq.com/tests", LinkOf(NotificationCards.Buttons(NotificationType.TestAssigned, null)));
        Assert.Equal("https://fibhq.com/aufgaben", LinkOf(NotificationCards.Buttons(NotificationType.JobAssigned, " ")));
        Assert.Equal("https://other.example/x/1", LinkOf(NotificationCards.Buttons(NotificationType.JobAssigned, "https://other.example/x/1")));

        static string LinkOf(ActionRowProperties? row) => row!.Components.OfType<LinkButtonProperties>().Single().Url;
    }

    [Fact]
    public void The_main_action_is_green_and_details_is_grey()
    {
        var buttons = NotificationCards.Buttons(NotificationType.JobAssigned, "/aufgaben/abc")!.Components.OfType<ButtonProperties>().ToList();

        Assert.Equal(ButtonStyle.Success, buttons[0].Style);
        Assert.Equal("Erledigt", buttons[0].Label);
        Assert.Equal(ButtonStyle.Secondary, buttons[1].Style);
        Assert.Equal("Details", buttons[1].Label);
    }

    [Fact]
    public void The_card_carries_the_buttons_of_its_category()
    {
        var card = NotificationCards.Generic(NotificationType.JobAssigned, BaseUrl, "/aufgaben/abc", null, "");

        Assert.Equal(["fib:cjd:abc", $"fib:nd:{(int)NotificationType.JobAssigned}:abc"], CustomIds(card));
        Assert.Equal(BaseUrl + "/aufgaben/abc", Assert.Single(Links(card)).Url);
    }

    [Fact]
    public void An_announcement_card_asks_for_acknowledgment_only_when_it_needs_one()
    {
        var plain = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", null, "");
        var needed = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", null, "", acknowledgeable: true);

        Assert.Equal([$"fib:nd:{(int)NotificationType.Announcement}:a1"], CustomIds(plain));
        Assert.Equal(["fib:cack:a1", $"fib:nd:{(int)NotificationType.Announcement}:a1"], CustomIds(needed));
        Assert.Equal(BaseUrl + "/brett/a1", Assert.Single(Links(needed)).Url);
    }

    [Fact]
    public void Custom_ids_fit()
    {
        var ids = new List<string>();
        foreach (var type in Enum.GetValues<NotificationType>())
        {
            foreach (var collection in new[] { "aufgaben", "brett", "bewerbungen", "hinweise", "tickets", "personen", "tests", "kalender" })
            {
                var href = $"/{collection}/{Guid}";
                ids.AddRange(CustomIds(NotificationCards.Generic(type, BaseUrl, href, null, "", acknowledgeable: true)));
                ids.AddRange(NotificationCards.Buttons(type, href, acknowledgeable: true)!.Components.OfType<ButtonProperties>().Select(b => b.CustomId));
            }
        }

        Assert.NotEmpty(ids);
        Assert.All(ids, id => Assert.InRange(id.Length, 1, 100));
        // longest shape present
        Assert.Contains(ids, id => id.StartsWith("fib:nd:") && id.EndsWith(":" + Guid));
        Assert.Contains(ids, id => id == $"fib:cjd:{Guid}");
        Assert.Contains(ids, id => id == $"fib:cack:{Guid}");
    }

    // ---------------------------------------------------------------- pings

    [Fact]
    public void PingLines_split_at_100()
    {
        var ids = Enumerable.Range(1, 250).Select(i => i.ToString()).ToList();

        var (first, rest) = NotificationCards.PingLines(ids);

        Assert.Equal(100, first.Split(' ').Length);
        Assert.StartsWith("<@1> <@2> ", first);
        Assert.EndsWith("<@100>", first);
        Assert.Equal([100, 50], rest.Select(l => l.Split(' ').Length).ToArray());
        Assert.StartsWith("<@101> ", rest[0]);
        Assert.EndsWith("<@250>", rest[1]);
    }

    [Fact]
    public void PingLines_keep_every_rest_line_a_postable_message()
    {
        // real ids are long
        var ids = Enumerable.Range(1, 250).Select(i => (100000000000000000L + i).ToString()).ToList();

        var (first, rest) = NotificationCards.PingLines(ids);

        Assert.All(rest, line => Assert.InRange(line.Length, 1, 2000));
        Assert.All(rest, line => Assert.True(line.Split(' ').Length <= 100));
        Assert.True(first.Split(' ').Length <= 100);
        var pinged = (first + " " + string.Join(' ', rest)).Split(' ');
        Assert.Equal(ids.Select(id => $"<@{id}>"), pinged);
    }

    [Fact]
    public void PingLines_honour_a_smaller_max()
    {
        var (first, rest) = NotificationCards.PingLines(["1", "2", "3", "4", "5"], 2);

        Assert.Equal("<@1> <@2>", first);
        Assert.Equal(["<@3> <@4>", "<@5>"], rest);
    }

    [Fact]
    public void PingLines_drop_duplicates_and_anything_that_is_no_snowflake()
    {
        var (first, rest) = NotificationCards.PingLines(["12", "12", "demo", "", "x> @everyone <@3", "34"]);

        Assert.Equal("<@12> <@34>", first);
        Assert.Empty(rest);
    }

    [Fact]
    public void PingLines_of_nobody_are_empty()
    {
        var (first, rest) = NotificationCards.PingLines([]);

        Assert.Equal(string.Empty, first);
        Assert.Empty(rest);
    }

    [Fact]
    public void A_role_ping_is_a_role_mention()
        => Assert.Equal("<@&1479854499853238475>", NotificationCards.RolePing("1479854499853238475"));

    [Theory]
    [InlineData("")]
    [InlineData("demo")]
    [InlineData("1> @everyone <@&2")]
    public void Anything_but_a_role_id_pings_nobody(string roleId)
        => Assert.Equal(string.Empty, NotificationCards.RolePing(roleId));

    // ---------------------------------------------------------------- the fit

    [Fact]
    public void Cards_fit_text_limit()
    {
        var reason = new string('x', 10_000);
        var termination = NotificationCards.Termination("Max", "<@1>", "<@2>", Moment, reason, BaseUrl + "/personal/p1");
        var entry = NotificationCards.PersonnelEntry("<@1>", "Max", "Verwarnung", Moment, reason, "A, B", BaseUrl + "/personal/p1");
        var decoration = NotificationCards.Decoration("<@1>", "Falke", "Verdienstkreuz", Moment, reason, "Chef", BaseUrl + "/personal/p1");
        var custom = NotificationCards.Custom(NotificationType.Announcement, reason, BaseUrl + "/bestenliste", "<@&1>");
        var digest = NotificationCards.Digest(reason, BaseUrl + "/benachrichtigungen");
        var share = NotificationCards.Share(reason);
        var generic = NotificationCards.Generic(NotificationType.Announcement, BaseUrl, "/brett/a1", reason, "<@&1>");

        foreach (var card in new[] { termination, entry, decoration, custom, digest, share, generic })
        {
            Assert.InRange(TextLength(card), 1, DiscordCards.MaxText);
        }
    }

    [Fact]
    public void A_cut_never_touches_the_ping()
    {
        var ids = Enumerable.Range(1, 100).Select(i => (100000000000000000L + i).ToString()).ToList();
        var (ping, _) = NotificationCards.PingLines(ids);

        var card = NotificationCards.Custom(NotificationType.Announcement, new string('x', 5000), BaseUrl + "/bestenliste", ping);

        Assert.Equal(ping, Texts(card)[0]);
        Assert.InRange(TextLength(card), 1, DiscordCards.MaxText);
        // body gave way
        Assert.EndsWith("…", Body(card));
    }

    // ---------------------------------------------------------------- special cards

    [Fact]
    public void A_personnel_entry_shows_what_today_s_embed_shows()
    {
        var card = NotificationCards.PersonnelEntry("<@1>", "Max Mustermann", "Verwarnung",
            new DateTime(2026, 11, 13, 18, 30, 0), "Zu spät\nzum Dienst", "Anna, Bob", BaseUrl + "/personal/p1");

        Assert.Equal("<@1>", Texts(card)[0]);
        Assert.Contains("### 📝 Personalakten\\-Eintrag", Texts(card));
        var body = Body(card);
        Assert.Contains("**👤 Name:** Max Mustermann", body);
        Assert.Contains("**🏷️ Art:** Verwarnung", body);
        Assert.Contains("**📅 Datum:** Freitag, 13. November 2026 um 18:30", body);
        Assert.Contains("**📋 Begründung:** Zu spät\nzum Dienst", body);
        Assert.Contains("**👮 Auszuführender:** Anna, Bob", body);
        Assert.Equal(DiscordCards.Bronze, Accent(card));
        Assert.Empty(CustomIds(card));
        Assert.Equal(BaseUrl + "/personal/p1", Assert.Single(Links(card)).Url);
        Assert.Equal(BaseUrl + "/FibIcon.png", Thumbnail(card));
    }

    [Fact]
    public void A_personnel_entry_escapes_what_agents_typed()
    {
        var card = NotificationCards.PersonnelEntry("<@1>", "**Max** <@999>", "_Art_", Moment,
            "@everyone <@&5> [x](http://evil)", "<@7>", null);

        var body = Body(card);
        Assert.DoesNotContain("<@999>", body);
        Assert.DoesNotContain("<@&5>", body);
        Assert.DoesNotContain("<@7>", body);
        Assert.DoesNotContain("](http", body);
        Assert.DoesNotContain("**Max**", body);
        // no link
        Assert.Empty(Links(card));
    }

    [Fact]
    public void An_empty_reason_or_executor_reads_as_a_dash()
    {
        var card = NotificationCards.PersonnelEntry("", "Max", "Notiz", Moment, "  ", "", BaseUrl + "/personal/p1");

        var body = Body(card);
        Assert.Contains("**📋 Begründung:** —", body);
        Assert.Contains("**👮 Auszuführender:** —", body);
        // no ping line
        Assert.StartsWith("### ", Texts(card)[0]);
    }

    [Fact]
    public void A_decoration_shows_what_today_s_embed_shows()
    {
        var card = NotificationCards.Decoration("<@1>", "Falke", "Verdienstkreuz", new DateTime(2026, 11, 13),
            "Für besondere Verdienste", "Chef Müller", BaseUrl + "/personal/p1");

        Assert.Equal("<@1>", Texts(card)[0]);
        Assert.Contains("### 🎖️ Ehrung", Texts(card));
        var body = Body(card);
        Assert.Contains("**👤 Agent:** Falke", body);
        Assert.Contains("**🎖️ Ehrenzeichen:** Verdienstkreuz", body);
        Assert.Contains("**📅 Verliehen am:** Freitag, 13. November 2026", body);
        Assert.Contains("**📜 Verleihungstext:** Für besondere Verdienste", body);
        Assert.Contains("**✍️ Verliehen durch:** Chef Müller", body);
        Assert.Equal(DiscordCards.Gold, Accent(card));
        Assert.Equal([], CustomIds(card));
        Assert.Equal(BaseUrl + "/personal/p1", Assert.Single(Links(card)).Url);
    }

    [Fact]
    public void A_decoration_without_citation_or_giver_skips_the_one_and_dashes_the_other()
    {
        var card = NotificationCards.Decoration("", "Falke", "Verdienstkreuz", Moment, "  ", null, null);

        var body = Body(card);
        Assert.DoesNotContain("Verleihungstext", body);
        Assert.Contains("**✍️ Verliehen durch:** —", body);
    }

    [Fact]
    public void A_termination_shows_chips_and_pings_nobody()
    {
        var card = NotificationCards.Termination("Max Mustermann", "<@1>", "<@2>",
            new DateTime(2026, 11, 13, 17, 30, 0, DateTimeKind.Utc),
            "Verstoß gegen <@3> die Dienstordnung", BaseUrl + "/personal/p1");

        var everything = Everything(card);
        Assert.Contains("### ⚠️ Kündigung", Texts(card));
        Assert.Contains("> Hiermit wird die **Kündigung** des folgenden Mitglieds offiziell bekannt gegeben.", everything);
        // name in bold, chip beside it
        Assert.Contains("**👤 Name:** **Max Mustermann** ( <@1> )", everything);
        Assert.Contains("**👮 Ausgeführt von:** <@2>", everything);
        // UTC to Berlin
        Assert.Contains("**📅 Datum:** Freitag, 13. November 2026 18:30", everything);
        // typed text inert
        Assert.DoesNotContain("<@3>", everything);
        Assert.Equal(DiscordCards.Danger, Accent(card));
        Assert.Equal([], CustomIds(card));
        Assert.Equal(BaseUrl + "/personal/p1", Assert.Single(Links(card)).Url);
        // no ping line
        Assert.StartsWith("### ", Texts(card)[0]);
    }

    [Fact]
    public void A_termination_without_a_chip_names_the_subject_in_bold_alone()
    {
        var card = NotificationCards.Termination("Max Mustermann", null, null, Moment, "Grund", null);

        var body = Body(card);
        Assert.Contains("**👤 Name:** **Max Mustermann**\n", body);
        Assert.DoesNotContain("( ", body);
        Assert.DoesNotContain("<@", Everything(card));
    }

    [Fact]
    public void A_termination_name_and_executor_are_typed_text()
    {
        var card = NotificationCards.Termination("**Max** <@5> @everyone", "kein Chip <@8>", "<@6> <@7>", Moment, "Grund", null);

        var body = Body(card);
        // bold around the escaped name only; a chip that is none is dropped
        Assert.Contains("**👤 Name:** **\\*\\*Max\\*\\* \\<\\@5\\> \\@everyone**\n", body);
        Assert.DoesNotContain("( ", body);
        Assert.DoesNotContain("<@", Everything(card));
    }

    [Fact]
    public void A_termination_with_a_blank_name_reads_as_a_dash()
    {
        var card = NotificationCards.Termination("  ", "<@1>", null, Moment, "Grund", null);

        Assert.Contains("**👤 Name:** **—** ( <@1> )", Body(card));
    }

    [Fact]
    public void A_termination_without_executor_reads_as_a_dash()
    {
        var card = NotificationCards.Termination("Max", null, null, Moment, "Grund", null);

        Assert.Contains("**👮 Ausgeführt von:** —", Body(card));
        Assert.Empty(Links(card));
    }

    // ---------------------------------------------------------------- wanted

    private static WantedDiscordEmbed WantedCard(string? image, string? thumbnail) => new(
        "🔎 Fahndung: Max\\_Muster",
        "/gesucht/FIB-2026-001",
        0xE53935,
        [
            new WantedDiscordField("Aktenzeichen", "FIB\\-2026\\-001", true),
            new WantedDiscordField("Gefahrenstufe", "Hoch", true),
            new WantedDiscordField("Kopfgeld", "$5.000", true),
            new WantedDiscordField("Auch bekannt als", "Maxi", false),
            new WantedDiscordField("Warnhinweise", "🔴 bewaffnet\n🟠 flüchtig", false),
        ],
        image, thumbnail, "Federal Investigation Bureau", Moment);

    [Fact]
    public void A_wanted_card_keeps_the_embed_content_and_its_photo()
    {
        var card = NotificationCards.Wanted(WantedCard("/gesucht/FIB-2026-001/foto", null), BaseUrl, "<@&9>");

        Assert.Equal("<@&9>", Texts(card)[0]);
        // title not escaped twice
        Assert.Contains("### 🔎 Fahndung: Max\\_Muster", Texts(card));
        var body = Body(card);
        Assert.Contains("**Aktenzeichen:** FIB\\-2026\\-001 · **Gefahrenstufe:** Hoch · **Kopfgeld:** $5.000", body);
        Assert.Contains("**Auch bekannt als:** Maxi", body);
        Assert.Contains("**Warnhinweise:**\n🔴 bewaffnet\n🟠 flüchtig", body);
        Assert.Equal(BaseUrl + "/gesucht/FIB-2026-001/foto", Thumbnail(card));
        Assert.Equal(DiscordCards.Orange, Accent(card));
        Assert.Equal([], CustomIds(card));
        Assert.Equal(BaseUrl + "/gesucht/FIB-2026-001", Assert.Single(Links(card)).Url);
        Assert.Equal(DiscordCards.Footer, Texts(card)[^1]);
        Assert.DoesNotContain("<t:", Everything(card));
    }

    [Fact]
    public void A_raised_bounty_uses_the_thumbnail_path()
    {
        var card = NotificationCards.Wanted(WantedCard(null, "/gesucht/FIB-2026-001/foto"), "https://fibhq.com/", "");

        Assert.Equal("https://fibhq.com/gesucht/FIB-2026-001/foto", Thumbnail(card));
        Assert.StartsWith("### ", Texts(card)[0]);
    }

    [Fact]
    public void A_wanted_card_without_a_photo_wears_the_logo()
    {
        var card = NotificationCards.Wanted(WantedCard(null, null), BaseUrl, "");

        Assert.Equal(BaseUrl + "/FibIcon.png", Thumbnail(card));
    }

    // ---------------------------------------------------------------- custom, digest, share

    [Fact]
    public void A_custom_card_carries_the_text_and_only_the_link()
    {
        const string content = "🏆 **Beste Agenten November**\n🥇 Falke · 12";
        var card = NotificationCards.Custom(NotificationType.Announcement, content, BaseUrl + "/bestenliste?zeitraum=2026-11", "<@&9>");

        Assert.Equal(["<@&9>", content], Prose(card));
        Assert.Equal("<@&9>", Texts(card)[0]);
        Assert.Contains("### 📢 Neue Ankündigung", Texts(card));
        Assert.Equal(DiscordCards.Bronze, Accent(card));
        // no id buttons
        Assert.Empty(CustomIds(card));
        Assert.Equal(BaseUrl + "/bestenliste?zeitraum=2026-11", Assert.Single(Links(card)).Url);
        Assert.Equal(BaseUrl + "/FibIcon.png", Thumbnail(card));
    }

    [Fact]
    public void A_custom_card_without_link_has_no_buttons()
    {
        var card = NotificationCards.Custom(NotificationType.PublicPressPublished, "📰 **Neue Pressemitteilung**", null, "");

        Assert.Empty(Links(card));
        Assert.Empty(CustomIds(card));
        Assert.Equal(DiscordCards.Sand, Accent(card));
        Assert.Equal(DiscordCards.Footer, Texts(card)[^1]);
    }

    [Fact]
    public void A_digest_card_carries_the_lines_that_hold_the_mentions()
    {
        const string content = "<@1> 3 neue Aufgaben\n<@2> 1 Erwähnung";
        var card = NotificationCards.Digest(content, BaseUrl + "/benachrichtigungen");

        Assert.Equal([content], Prose(card));
        Assert.Contains("### 🗞️ Tagesüberblick", Texts(card));
        Assert.Equal(DiscordCards.Steel, Accent(card));
        Assert.Empty(CustomIds(card));
        Assert.Equal(BaseUrl + "/benachrichtigungen", Assert.Single(Links(card)).Url);
        // mentions in body
        Assert.StartsWith("### ", Texts(card)[0]);
    }

    [Fact]
    public void A_share_card_is_the_text_and_the_footer()
    {
        const string content = "📎 Geteilte Akte: Person „Max“ · FIB\\-1\nhttps://fibhq.com/personen/abc";
        var card = NotificationCards.Share(content);

        Assert.Equal([content, DiscordCards.Footer], Texts(card));
        Assert.Empty(CustomIds(card));
        Assert.Empty(Links(card));
        Assert.Equal(DiscordCards.Steel, Accent(card));
    }
}
