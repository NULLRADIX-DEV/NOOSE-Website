using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services;

/// <summary>What a shared record looks like in Discord, which webhooks are accepted and how the channel list is kept.</summary>
public sealed class ChannelShareRulesTests
{
    private const string Hook = "https://discord.com/api/webhooks/123456789012345678/abcDEF-ghi_jkl";
    private const string Token = "abcDEF-ghi_jkl";
    private static readonly string Prefix = CaseNumberService.AgencyPrefix;

    // ---- webhook URLs ----

    [Theory]
    [InlineData(Hook)]
    [InlineData("https://discordapp.com/api/webhooks/1/tok")]
    [InlineData("https://ptb.discord.com/api/webhooks/1/tok")]
    [InlineData("https://canary.discord.com/api/webhooks/1/tok")]
    [InlineData("https://discord.com/api/v10/webhooks/1/tok")]
    [InlineData("https://discord.com/api/webhooks/1/tok?thread_id=99")]
    [InlineData("  https://discord.com/api/webhooks/1/tok  ")]
    public void A_discord_webhook_is_accepted(string url) => Assert.True(ChannelShareRules.IsWebhookUrl(url));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://discord.com/api/webhooks/1/tok")]
    [InlineData("https://discord.com.evil.example/api/webhooks/1/tok")]
    [InlineData("https://evil.example/api/webhooks/1/tok")]
    [InlineData("https://user:pw@discord.com/api/webhooks/1/tok")]
    [InlineData("https://discord.com:8443/api/webhooks/1/tok")]
    [InlineData("https://discord.com/api/webhooks/1/tok?wait=true")]
    [InlineData("https://discord.com/api/webhooks/1/tok?thread_id=abc")]
    [InlineData("https://discord.com/api/webhooks/1/tok#x")]
    [InlineData("https://discord.com/api/webhooks/abc/tok")]
    [InlineData("https://discord.com/api/webhooks/1")]
    [InlineData("https://discord.com/api/webhooks/1/tok/github")]
    [InlineData("https://discord.com/api/channels/1/tok")]
    public void Anything_else_is_refused(string? url) => Assert.False(ChannelShareRules.IsWebhookUrl(url));

    [Fact]
    public void The_mask_shows_the_webhook_id_and_never_the_token()
    {
        var masked = ChannelShareRules.Mask(Hook);

        Assert.Contains("123456789012345678", masked);
        Assert.DoesNotContain(Token, masked);
        Assert.Contains("(Thread)", ChannelShareRules.Mask(Hook + "?thread_id=5"));
        Assert.Equal("hinterlegt", ChannelShareRules.Mask("kaputt"));
    }

    // ---- note ----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    public void An_empty_note_is_no_note(string? note) => Assert.Null(ChannelShareRules.CleanNote(note));

    [Fact]
    public void A_note_becomes_one_line_without_control_characters()
        => Assert.Equal("Bitte prüfen heute", ChannelShareRules.CleanNote(" Bitte\r\n prüfen\t\u0007heute "));

    [Fact]
    public void A_note_is_capped()
    {
        Assert.NotNull(ChannelShareRules.CleanNote(new string('a', ChannelShareRules.MaxNoteLength)));
        Assert.Throws<InvalidOperationException>(
            () => ChannelShareRules.CleanNote(new string('a', ChannelShareRules.MaxNoteLength + 1)));
    }

    // ---- post ----

    [Fact]
    public void The_post_names_type_name_and_case_number_quotes_the_note_and_ends_with_the_link()
    {
        var post = ChannelShareRules.Compose(nameof(Person), "Max Mustermann", $"{Prefix}-P-2026-0001", "Bitte prüfen",
            "https://fibhq.com/personen/p1");

        var lines = post.Split('\n');
        Assert.Equal(3, lines.Length);
        Assert.Contains("Person „Max Mustermann“", lines[0]);
        Assert.Contains($"{Prefix}-P-2026-0001", lines[0]);
        Assert.Equal("> Bitte prüfen", lines[1]);
        Assert.Equal("https://fibhq.com/personen/p1", lines[2]);
    }

    [Fact]
    public void Without_a_note_there_is_no_quote()
    {
        var post = ChannelShareRules.Compose(nameof(Case), "Ermittlung", $"{Prefix}-V-2026-0001", null, "https://x/vorgaenge/c");

        Assert.Equal(2, post.Split('\n').Length);
        Assert.Contains("Vorgang „Ermittlung“", post);
    }

    [Fact]
    public void Free_text_cannot_format_link_or_ping()
    {
        var post = ChannelShareRules.Compose(nameof(Person), "**Max**\n# [Klick](https://evil.example) <@&1>",
            $"{Prefix}-P-1", "`code` > zitat <@123> [x](https://evil.example) ||spoiler||", "https://fibhq.com/personen/p1");

        // newlines flattened
        Assert.Equal(3, post.Split('\n').Length);
        Assert.DoesNotContain("](", post);
        // no mention chips
        Assert.DoesNotMatch(@"(?<!\\)<", post);
        Assert.DoesNotContain("**Max**", post);
        Assert.DoesNotContain("`code`", post);
        Assert.DoesNotContain("||spoiler||", post);
    }

    [Fact]
    public void A_long_name_is_cut()
    {
        var post = ChannelShareRules.Compose(nameof(Person), new string('x', 500), $"{Prefix}-P-1", null, "https://x/p");

        Assert.True(post.Length < 300);
        Assert.Contains("…", post);
    }

    [Fact]
    public void The_link_points_at_the_detail_page_under_the_base_url()
    {
        Assert.Equal("https://fibhq.com/personen/p1", ChannelShareRules.Link("https://fibhq.com/", nameof(Person), "p1"));
        Assert.Equal("https://fibhq.com/vorgaenge/c%2F1", ChannelShareRules.Link("https://fibhq.com", nameof(Case), "c/1"));
    }

    // ---- secrecy ----

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    public void Any_secrecy_flag_restricts(bool classified, bool tru, bool hrb, bool restricted)
    {
        var person = new Person { IsClassified = classified, IsTRUClassified = tru, IsHRBClassified = hrb };

        Assert.Equal(restricted, ChannelShareRules.IsRestricted(person));
    }

    // ---- stored list ----

    [Fact]
    public void A_broken_blob_reads_as_no_channel()
    {
        Assert.Empty(ChannelShareRules.Parse(null));
        Assert.Empty(ChannelShareRules.Parse("{nicht json"));
        Assert.Empty(ChannelShareRules.Parse("""[{"Id":"a","Name":"X","Url":"https://evil.example/api/webhooks/1/t"}]"""));
    }

    [Fact]
    public void The_list_survives_a_round_trip()
    {
        var stored = new List<StoredShareChannel> { new("a", "Ermittlungen", Hook) };

        Assert.Equal(stored, ChannelShareRules.Parse(ChannelShareRules.Serialize(stored)));
    }

    [Fact]
    public void The_version_follows_the_stored_text_and_hides_it()
    {
        var json = ChannelShareRules.Serialize([new StoredShareChannel("a", "Alt", Hook)]);
        var replaced = json.Replace("Alt", "Neu");

        Assert.Equal(ChannelShareRules.Version(json), ChannelShareRules.Version(json));
        Assert.NotEqual(ChannelShareRules.Version(json), ChannelShareRules.Version(replaced));
        // no row reads like an empty one
        Assert.Equal(ChannelShareRules.Version(null), ChannelShareRules.Version(string.Empty));
        Assert.DoesNotContain("discord", ChannelShareRules.Version(json), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_empty_url_keeps_the_stored_webhook_and_a_new_one_replaces_it()
    {
        var stored = new List<StoredShareChannel> { new("a", "Alt", Hook), new("b", "Zwei", Hook) };
        var replacement = "https://discord.com/api/webhooks/1/neu";

        var (channels, replaced) = ChannelShareRules.Merge(stored,
        [
            new ShareChannelInput { Id = "a", Name = " Neu ", WebhookUrl = "" },
            new ShareChannelInput { Id = "b", Name = "Zwei", WebhookUrl = replacement },
        ], () => "x");

        Assert.Equal(new StoredShareChannel("a", "Neu", Hook), channels[0]);
        Assert.Equal(new StoredShareChannel("b", "Zwei", replacement), channels[1]);
        Assert.Equal(new[] { "Zwei" }, replaced);
    }

    [Fact]
    public void A_missing_row_is_removed_and_a_new_one_gets_a_server_id()
    {
        var stored = new List<StoredShareChannel> { new("a", "Alt", Hook) };

        var (channels, _) = ChannelShareRules.Merge(stored,
            [new ShareChannelInput { Id = "fremd", Name = "Neu", WebhookUrl = Hook }], () => "server-id");

        var only = Assert.Single(channels);
        Assert.Equal("server-id", only.Id);
    }

    [Fact]
    public void A_new_channel_needs_a_url() => Assert.Throws<InvalidOperationException>(() =>
        ChannelShareRules.Merge([], [new ShareChannelInput { Name = "Neu" }], () => "x"));

    [Fact]
    public void Names_are_required_short_and_unique()
    {
        Assert.Throws<InvalidOperationException>(() =>
            ChannelShareRules.Merge([], [new ShareChannelInput { Name = " ", WebhookUrl = Hook }], () => "x"));
        Assert.Throws<InvalidOperationException>(() => ChannelShareRules.Merge([],
            [new ShareChannelInput { Name = new string('n', ChannelShareRules.MaxNameLength + 1), WebhookUrl = Hook }],
            () => "x"));
        Assert.Throws<InvalidOperationException>(() => ChannelShareRules.Merge([],
        [
            new ShareChannelInput { Name = "Ermittlung", WebhookUrl = Hook },
            new ShareChannelInput { Name = "ermittlung", WebhookUrl = Hook },
        ], () => Guid.NewGuid().ToString()));
    }

    [Fact]
    public void A_foreign_url_and_too_many_channels_are_refused()
    {
        Assert.Throws<InvalidOperationException>(() => ChannelShareRules.Merge([],
            [new ShareChannelInput { Name = "X", WebhookUrl = "https://evil.example/api/webhooks/1/t" }], () => "x"));
        var many = Enumerable.Range(0, ChannelShareRules.MaxChannels + 1)
            .Select(i => new ShareChannelInput { Name = $"K{i}", WebhookUrl = Hook }).ToList();
        Assert.Throws<InvalidOperationException>(() => ChannelShareRules.Merge([], many, () => Guid.NewGuid().ToString()));
    }

    [Fact]
    public void The_same_stored_channel_twice_is_refused() => Assert.Throws<InvalidOperationException>(() =>
        ChannelShareRules.Merge([new StoredShareChannel("a", "A", Hook)],
        [
            new ShareChannelInput { Id = "a", Name = "A" },
            new ShareChannelInput { Id = "a", Name = "B" },
        ], () => "x"));

    // ---- quota ----

    [Fact]
    public void The_quota_allows_a_few_shares_per_window_and_per_agent()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero));
        var quota = new ChannelShareQuota(clock);

        for (var i = 0; i < ChannelShareQuota.PerWindow; i++)
        {
            quota.Take("a");
        }
        Assert.Throws<InvalidOperationException>(() => quota.Take("a"));
        quota.Take("b");

        clock.Now += ChannelShareQuota.Window;
        quota.Take("a");
    }

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
