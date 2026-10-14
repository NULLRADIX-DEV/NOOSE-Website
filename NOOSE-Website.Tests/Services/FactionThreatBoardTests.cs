using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>The threat board's pure rules: threshold, order, wording, fingerprint and the message bookkeeping.</summary>
public class FactionThreatBoardTests
{
    private const string Base = "https://noose.info";
    private static readonly DateTime At = new(2026, 10, 14, 16, 42, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, 90)]
    [InlineData("", 90)]
    [InlineData("abc", 90)]
    [InlineData("-1", 90)]
    [InlineData("100", 90)]
    [InlineData("0", 0)]
    [InlineData("85", 85)]
    [InlineData("99", 99)]
    public void ParseThreshold_falls_back_to_ninety_outside_the_range(string? raw, int expected)
        => Assert.Equal(expected, FactionThreatBoard.ParseThreshold(raw));

    [Fact]
    public void Order_puts_the_highest_score_first_and_breaks_ties_by_name()
    {
        var ordered = FactionThreatBoard.Order(
        [
            new("b", "Vagos", 93),
            new("a", "ballas", 97),
            new("c", "Aztecas", 93),
        ]);

        Assert.Equal(["ballas", "Aztecas", "Vagos"], ordered.Select(e => e.Name));
    }

    [Fact]
    public void An_empty_board_says_so_in_green()
    {
        var embed = FactionThreatBoard.Render([], 90, Base, At);

        Assert.Equal("🚨 GEFAHRENLAGE · FRAKTIONEN ÜBER 90", embed.Title);
        Assert.Contains("keine Fraktion über 90", embed.Description);
        Assert.Equal(0x43A047, embed.Color);
        Assert.Equal(At, embed.AsOfUtc);
    }

    [Fact]
    public void A_line_links_the_file_and_shows_the_score()
    {
        var embed = FactionThreatBoard.Render([new("f-1", "Los Santos Vagos", 97)], 90, Base + "/", At);

        Assert.Contains("🔴 `97` **[Los Santos Vagos](https://noose.info/fraktionen/f-1)**", embed.Description);
        Assert.Equal(0xE53935, embed.Color);
        Assert.StartsWith("1 Fraktion ·", embed.Footer);
    }

    [Fact]
    public void Footer_counts_in_the_plural()
    {
        var embed = FactionThreatBoard.Render([new("a", "A", 95), new("b", "B", 92)], 90, Base, At);

        Assert.StartsWith("2 Fraktionen ·", embed.Footer);
    }

    [Fact]
    public void A_name_cannot_break_out_of_its_link_or_ping_anyone()
    {
        var embed = FactionThreatBoard.Render([new("x", "Evil](https://x.test) <@123> **fett**", 95)], 90, Base, At);

        // Discord pairs link brackets before it reads escapes, so they are swapped, not escaped
        Assert.Contains(@"Evil］\(https", embed.Description);
        Assert.DoesNotContain("Evil]", embed.Description);
        Assert.Contains(@"\<@123\>", embed.Description);
        Assert.Contains(@"\*\*fett\*\*", embed.Description);
    }

    [Fact]
    public void A_lone_bracket_from_cutting_a_tag_cannot_break_the_link()
    {
        var name = new string('x', 55) + " [ABC]";
        var embed = FactionThreatBoard.Render([new("x", name, 95)], 90, Base, At);

        var line = embed.Description.Split('\n').Single(l => l.StartsWith("🔴"));
        Assert.Equal(1, line.Count(c => c == '['));
        Assert.Equal(1, line.Count(c => c == ']'));
        Assert.Contains("［AB…](", line);
    }

    [Fact]
    public void Cutting_never_splits_an_emoji()
    {
        var name = new string('x', FactionThreatBoard.MaxNameChars - 2) + "🔥🔥🔥";
        var embed = FactionThreatBoard.Render([new("x", name, 95)], 90, Base, At);

        // a strict encoder throws on half an emoji
        _ = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetBytes(embed.Description);
        Assert.Contains("x…](", embed.Description);
    }

    [Theory]
    [InlineData(404, null, ThreatBoardEdit.Missing)]
    [InlineData(404, 10008, ThreatBoardEdit.Missing)]
    [InlineData(404, FactionThreatBoard.UnknownWebhookCode, ThreatBoardEdit.Missing)]
    [InlineData(400, FactionThreatBoard.ArchivedThreadCode, ThreatBoardEdit.Archived)]
    [InlineData(400, 50035, ThreatBoardEdit.Failed)]
    [InlineData(401, 50027, ThreatBoardEdit.Failed)]
    [InlineData(403, null, ThreatBoardEdit.Failed)]
    [InlineData(429, null, ThreatBoardEdit.Failed)]
    [InlineData(500, null, ThreatBoardEdit.Failed)]
    public void A_refused_edit_reposts_only_when_the_message_is_gone_for_good(int status, int? code, ThreatBoardEdit expected)
        => Assert.Equal(expected, FactionThreatBoard.RefusedEdit(status, code));

    [Fact]
    public void A_long_name_is_cut_and_line_breaks_flatten()
    {
        var name = "Erste Zeile\r\nzweite " + new string('x', 100);
        var embed = FactionThreatBoard.Render([new("x", name, 95)], 90, Base, At);

        Assert.DoesNotContain("\r", embed.Description);
        Assert.Contains("Erste Zeile zweite", embed.Description);
        Assert.Contains("…]", embed.Description);
    }

    [Fact]
    public void More_than_the_line_cap_collapses_into_a_count()
    {
        var entries = Enumerable.Range(0, 30).Select(i => new ThreatBoardEntry($"id-{i:00}", $"Fraktion {i:00}", 99)).ToList();

        var embed = FactionThreatBoard.Render(FactionThreatBoard.Order(entries), 90, Base, At);

        Assert.Equal(FactionThreatBoard.MaxLines, embed.Description.Split('\n').Count(l => l.StartsWith("🔴")));
        Assert.EndsWith("… und 5 weitere", embed.Description);
        Assert.StartsWith("30 Fraktionen", embed.Footer);
    }

    [Fact]
    public void The_description_stays_inside_discords_limit_with_long_names()
    {
        var entries = Enumerable.Range(0, 25)
            .Select(i => new ThreatBoardEntry(Guid.NewGuid().ToString(), new string('*', 80) + i, 99))
            .ToList();

        var embed = FactionThreatBoard.Render(entries, 90, "https://a-rather-long-base-url.example.test", At);

        Assert.True(embed.Description.Length <= 4096, $"{embed.Description.Length} Zeichen");
        Assert.Contains("weitere", embed.Description);
    }

    [Fact]
    public void Fingerprint_ignores_the_time_but_sees_every_visible_change()
    {
        IReadOnlyList<ThreatBoardEntry> list = [new("a", "Ballas", 93)];
        var fp = FactionThreatBoard.Fingerprint(list, 90, Base);

        Assert.Equal(fp, FactionThreatBoard.Fingerprint([new("a", "Ballas", 93)], 90, Base));
        Assert.NotEqual(fp, FactionThreatBoard.Fingerprint([new("a", "Ballas", 94)], 90, Base));
        Assert.NotEqual(fp, FactionThreatBoard.Fingerprint([new("a", "Ballas MC", 93)], 90, Base));
        Assert.NotEqual(fp, FactionThreatBoard.Fingerprint(list, 85, Base));
        Assert.NotEqual(fp, FactionThreatBoard.Fingerprint(list, 90, "https://demo.noose.info"));
        Assert.NotEqual(fp, FactionThreatBoard.Fingerprint([], 90, Base));
        Assert.Matches("^[0-9a-f]{64}$", fp);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/123456/tok-en", "123456")]
    [InlineData("https://discordapp.com/api/v10/webhooks/987/abc?thread_id=5", "987")]
    public void WebhookId_reads_the_id_from_the_url(string url, string expected)
        => Assert.Equal(expected, FactionThreatBoard.WebhookId(url));

    [Fact]
    public void WebhookId_of_an_odd_url_is_a_stable_hash()
    {
        var id = FactionThreatBoard.WebhookId("https://proxy.test/hook");

        Assert.StartsWith("h", id);
        Assert.Equal(id, FactionThreatBoard.WebhookId("https://proxy.test/hook"));
        Assert.NotEqual(id, FactionThreatBoard.WebhookId("https://proxy.test/other"));
    }

    [Fact]
    public void The_stored_message_keeps_the_url_it_went_out_through()
    {
        const string url = "https://discord.com/api/webhooks/1/t?thread_id=9";

        Assert.Equal(new ThreatBoardMessage("555", url), FactionThreatBoard.ParseRef(FactionThreatBoard.MessageRef("555", url)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("555")]
    [InlineData("555|")]
    [InlineData("|https://x.test")]
    [InlineData("55x|https://x.test")]
    public void A_damaged_reference_reads_as_none(string? raw)
        => Assert.Null(FactionThreatBoard.ParseRef(raw));

    [Theory]
    [InlineData(404, null, ThreatBoardRemoval.Removed)]
    [InlineData(404, 10008, ThreatBoardRemoval.Removed)]
    // the webhook is gone, its message is not
    [InlineData(404, FactionThreatBoard.UnknownWebhookCode, ThreatBoardRemoval.Unreachable)]
    [InlineData(401, 50027, ThreatBoardRemoval.Unreachable)]
    [InlineData(403, null, ThreatBoardRemoval.Unreachable)]
    [InlineData(400, FactionThreatBoard.ArchivedThreadCode, ThreatBoardRemoval.Unreachable)]
    [InlineData(429, null, ThreatBoardRemoval.Failed)]
    [InlineData(500, null, ThreatBoardRemoval.Failed)]
    public void A_refused_delete_retries_only_what_may_still_work(int status, int? code, ThreatBoardRemoval expected)
        => Assert.Equal(expected, FactionThreatBoard.RefusedDelete(status, code));

    [Theory]
    [InlineData("https://d.test/webhooks/1/new", "https://d.test/webhooks/1/old?thread_id=9", "https://d.test/webhooks/1/new?thread_id=9")]
    [InlineData("https://d.test/webhooks/1/new?thread_id=5", "https://d.test/webhooks/1/old", "https://d.test/webhooks/1/new")]
    [InlineData("https://d.test/webhooks/1/new", "https://d.test/webhooks/1/old", "https://d.test/webhooks/1/new")]
    public void WithQueryOf_takes_the_path_of_one_and_the_thread_of_the_other(string url, string queryFrom, string expected)
        => Assert.Equal(expected, FactionThreatBoard.WithQueryOf(url, queryFrom));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/t", "https://discord.com/api/webhooks/1/t?wait=true")]
    [InlineData("https://discord.com/api/webhooks/1/t?thread_id=9", "https://discord.com/api/webhooks/1/t?thread_id=9&wait=true")]
    public void PostUrl_asks_discord_to_answer_with_the_message(string url, string expected)
        => Assert.Equal(expected, FactionThreatBoard.PostUrl(url));

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/t", "https://discord.com/api/webhooks/1/t/messages/55")]
    [InlineData("https://discord.com/api/webhooks/1/t/", "https://discord.com/api/webhooks/1/t/messages/55")]
    [InlineData("https://discord.com/api/webhooks/1/t?thread_id=9", "https://discord.com/api/webhooks/1/t/messages/55?thread_id=9")]
    public void EditUrl_keeps_the_thread_query(string url, string expected)
        => Assert.Equal(expected, FactionThreatBoard.EditUrl(url, "55"));
}
