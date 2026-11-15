using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetCord.Rest;
using NOOSE_Website.Data.Entities.Regulation;
using NOOSE_Website.Infrastructure.Discord.Bot;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;
using static NOOSE_Website.Tests.Services.Integration.RegulationFixture;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>Service regulation changes as one collected Discord post: Markdown, layout, split, collector and worker.</summary>
public class RegulationChangePostTests
{
    private const string BaseUrl = "https://fibhq.com";
    private const string Role = "123456789012345678";
    private const string Ins = "<ins class=\"noosei-neu\">";
    private const string Del = "<del class=\"noosei-weg\">";
    private static readonly DateTime Since = Stamp.AddDays(10);

    // ---------------------------------------------------------------- helpers

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

    private static List<string> Texts(IEnumerable<IMessageComponentProperties> message)
        => Walk(message).OfType<TextDisplayProperties>().Select(t => t.Content).ToList();

    private static string Everything(IEnumerable<IMessageComponentProperties> message) => string.Join("\n", Texts(message));

    private static RegulationChange Changed(string label, string diff, params string[] publishers)
        => new(label, RegulationChangeKind.Changed, diff, null, null, null, publishers);

    private static IReadOnlyList<IReadOnlyList<IMessageComponentProperties>> Render(IReadOnlyList<RegulationChange> changes,
        IReadOnlyList<(string Chip, string Rank)>? signers = null)
        => RegulationChangePost.Render(changes, Role, signers ?? [("<@111111111111111111>", "Deputy Director")], BaseUrl);

    // long changes that cannot share one message
    private static List<RegulationChange> LongChanges(int count = 6, int length = 1500)
        => Enumerable.Range(1, count)
            .Select(i => Changed($"§{i}.1 Absatz {i}", $"<p>{new string('x', length)} {Ins}neu{i}</ins></p>"))
            .ToList();

    // ---------------------------------------------------------------- markdown

    [Fact]
    public void Markdown_turns_ins_bold_and_del_struck()
    {
        Assert.Equal("a **b** ~~c~~", RegulationChangePost.Markdown($"a {Ins}b</ins> {Del}c</del>"));
        Assert.Equal("eins **zwei**\ndrei", RegulationChangePost.Markdown($"<p>eins {Ins}zwei</ins></p><p>drei</p>"));
        // other tags stripped, user text escaped
        Assert.Equal(@"x \*y\* **\<z\>**", RegulationChangePost.Markdown($"<p><span>x</span> *y* {Ins}&lt;z&gt;</ins></p>"));
        // spaces stay outside the marks
        Assert.Equal("a **b** c", RegulationChangePost.Markdown($"a{Ins} b </ins>c"));
    }

    // ---------------------------------------------------------------- layout

    [Fact]
    public void Corrections_go_under_Sonstiges()
    {
        var messages = Render([
            Changed("§5.1 Dienstkleidung", $"<p>Anzug {Ins}schwarz</ins></p>"),
            new RegulationChange("§6.3 Dienstnummer", RegulationChangeKind.Correction, "<p>geheimer Wortlaut</p>",
                "<p>geheimer Wortlaut</p>", "Querverweise korrigiert", null, []),
        ]);

        var message = Assert.Single(messages);
        Assert.Contains("\n\n### §5.1 Dienstkleidung\nAnzug **schwarz**", Everything(message));
        Assert.Contains("\n\n**Sonstiges**\n> §6.3 Dienstnummer: Querverweise korrigiert", Everything(message));
        Assert.DoesNotContain("### §6.3", Everything(message));
        Assert.DoesNotContain("geheimer", Everything(message));
        // legend only with a change
        Assert.Contains("**Fett:** Neuerung / Änderung\n~~Durchgestrichen:~~ Entfernungen", Everything(message));
    }

    [Fact]
    public void Introduced_shows_full_text_as_quote()
    {
        var message = Assert.Single(Render([
            new RegulationChange("§9.3 Sector X", RegulationChangeKind.Introduced, null,
                "<p>Der Bunker liegt hier.</p><p>Zweite Zeile.</p>", null, null, []),
        ]));

        Assert.Contains("\n\n**Neueinführung: §9.3 Sector X**\n> Der Bunker liegt hier.\n> Zweite Zeile.", Everything(message));
    }

    [Fact]
    public void Repealed_shows_reason()
    {
        var message = Assert.Single(Render([
            new RegulationChange("§2.1 Altes", RegulationChangeKind.Repealed, null, null, null, "Überholt durch §2.2", []),
        ]));

        Assert.Contains("\n\n**Aufgehoben: §2.1 Altes**\n> Überholt durch §2.2", Everything(message));
    }

    [Fact]
    public void First_message_pings_role_only_once()
    {
        var messages = Render(LongChanges());

        Assert.True(messages.Count >= 2);
        Assert.Contains($"<@&{Role}>", Everything(messages[0]));
        Assert.StartsWith("# Änderung der Dienstverordnung\n", Texts(messages[0])[0]);
        Assert.All(messages.Skip(1), m => Assert.DoesNotContain("<@&", Everything(m)));
        // split at paragraph boundaries only
        var all = string.Join("\n", messages.Select(Everything));
        Assert.All(Enumerable.Range(1, 6), i => Assert.Contains($"### §{i}.1 Absatz {i}\n", all));
        Assert.All(Enumerable.Range(1, 6), i => Assert.Contains($"**neu{i}**", all));
    }

    [Fact]
    public void Every_message_fits()
    {
        var changes = LongChanges();
        changes.Insert(2, Changed("§3.9 Riesig", $"<p>{new string('y', 9000)}</p>"));

        var messages = Render(changes);

        Assert.All(messages, m => Assert.True(Texts(m).Sum(t => t.Length) <= DiscordCards.MaxText));
        Assert.Contains("Mit freundlichen Grüßen", Everything(messages[^1]));
    }

    [Fact]
    public void Signers_are_chips_with_rank()
    {
        var messages = Render(LongChanges(), [("<@111111111111111111>", "Deputy Director"), (@"Falke\-2", "Special Agent")]);

        Assert.Contains("Mit freundlichen Grüßen\n<@111111111111111111> | Deputy Director\nFalke\\-2 | Special Agent",
            Everything(messages[^1]));
        Assert.All(messages.SkipLast(1), m => Assert.DoesNotContain("Mit freundlichen Grüßen", Everything(m)));
    }

    [Fact]
    public void Buttons_are_ack_list_and_open()
    {
        var messages = Render(LongChanges());

        var last = Walk(messages[^1]).ToList();
        var ack = Assert.Single(last.OfType<ButtonProperties>());
        Assert.Equal("fib:hub:ack", ack.CustomId);
        Assert.Equal("Zur Kenntnis nehmen", ack.Label);
        var open = Assert.Single(last.OfType<LinkButtonProperties>());
        Assert.Equal($"{BaseUrl}/dienstverordnung", open.Url);
        Assert.Equal("Öffnen", open.Label);
        Assert.All(messages.SkipLast(1), m => Assert.Empty(Walk(m).OfType<ActionRowProperties>()));
    }

    [Fact]
    public void Many_short_changes_stay_within_the_component_limit()
    {
        var repeals = Enumerable.Range(1, 50)
            .Select(i => new RegulationChange($"§{i}.1 Absatz {i}", RegulationChangeKind.Repealed, null, null, null, "Überholt", []))
            .ToList();

        var messages = Render(repeals);

        // Discord's limit
        Assert.All(messages, m => Assert.True(Walk(m).Count() <= 40));
        Assert.All(messages, m => Assert.True(Texts(m).Sum(t => t.Length) <= DiscordCards.MaxText));
        var all = string.Join("\n", messages.Select(Everything));
        Assert.All(Enumerable.Range(1, 50), i => Assert.Contains($"**Aufgehoben: §{i}.1 Absatz {i}**\n> Überholt", all));
    }

    [Fact]
    public void Nothing_renders_nothing() => Assert.Empty(Render([]));

    // ---------------------------------------------------------------- collector

    private static RegulationParagraphVersion By(RegulationParagraphVersion version, string? agentId, bool ack = true,
        string note = "Notiz")
    {
        version.PublishedById = agentId;
        version.PublishedByName = agentId is null ? null : "Snapshot-" + agentId;
        version.RequiresAcknowledgment = ack;
        version.ChangeNote = note;
        return version;
    }

    private static async Task SeedRegulationAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.RegulationSections.AddRange(SectionRow("s1", 10, "Erster"), SectionRow("s9", 90, "Papierkorb",
            configure: s => s.IsDeleted = true));
        db.RegulationParagraphs.AddRange(
            ParagraphRow("p1", "s1", "Dienstkleidung", configure: p => { p.SortOrder = 10; p.CurrentVersion = 3; }),
            ParagraphRow("p2", "s1", "Dienstnummer", configure: p => { p.SortOrder = 20; p.CurrentVersion = 2; }),
            ParagraphRow("p3", "s1", "Sector X", configure: p => p.SortOrder = 30),
            ParagraphRow("p4", "s1", "Altes", configure: p =>
            {
                p.SortOrder = 40;
                p.RepealedAt = Since.AddHours(3);
                p.RepealedById = "a3";
                p.RepealedByName = "Snapshot-a3";
                p.RepealReason = "Überholt";
            }),
            ParagraphRow("p5", "s1", "Ruhig", configure: p => { p.SortOrder = 50; p.CurrentVersion = 2; }),
            ParagraphRow("p6", "s9", "Im Papierkorb", configure: p => p.CurrentVersion = 2),
            ParagraphRow("p7", "s1", "Gelöscht", configure: p => { p.CurrentVersion = 2; p.IsDeleted = true; }));
        db.RegulationVersions.AddRange(
            By(VersionRow("p1", 1, Stamp, "<p>Anzug grau</p>"), "a0"),
            By(VersionRow("p1", 2, Since.AddHours(1), "<p>Anzug blau</p>"), "a1"),
            By(VersionRow("p1", 3, Since.AddHours(2), "<p>Anzug schwarz</p>"), "a2"),
            By(VersionRow("p2", 1, Stamp), "a0"),
            By(VersionRow("p2", 2, Since.AddMinutes(10)), "a1", ack: false, note: "Querverweise korrigiert"),
            By(VersionRow("p3", 1, Since.AddMinutes(30), "<p>Der Bunker</p>"), "a1"),
            By(VersionRow("p4", 1, Stamp), "a0"),
            By(VersionRow("p5", 1, Stamp), "a0"),
            By(VersionRow("p5", 2, Since.AddMinutes(-5)), "a0"),
            By(VersionRow("p6", 1, Stamp), "a0"),
            By(VersionRow("p6", 2, Since.AddHours(5)), "a1"),
            By(VersionRow("p7", 1, Stamp), "a0"),
            By(VersionRow("p7", 2, Since.AddHours(5)), "a1"));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Collector_reads_each_paragraph_once_since_the_state()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);
        var collector = new RegulationChangeCollector(ctx.Factory);

        var (changes, newest) = await collector.CollectAsync(Since);

        string Label(string heading) => RegulationRules.ParagraphLabel(1, heading);
        // document order; p5 is older than the state, p6 and p7 are hidden
        Assert.Equal([Label("Dienstkleidung"), Label("Dienstnummer"), Label("Sector X"), Label("Altes")],
            changes.Select(c => c.Label));
        var changed = changes[0];
        Assert.Equal(RegulationChangeKind.Changed, changed.Kind);
        Assert.Equal(HtmlDiff.Compare("<p>Anzug grau</p>", "<p>Anzug schwarz</p>").Html, changed.DiffHtml);
        Assert.Equal(["a1", "a2"], changed.PublisherIds);
        var correction = changes[1];
        Assert.Equal(RegulationChangeKind.Correction, correction.Kind);
        Assert.Equal("Querverweise korrigiert", correction.Note);
        Assert.Null(correction.DiffHtml);
        Assert.Null(correction.ContentHtml);
        var introduced = changes[2];
        Assert.Equal(RegulationChangeKind.Introduced, introduced.Kind);
        Assert.Equal("<p>Der Bunker</p>", introduced.ContentHtml);
        var repealed = changes[3];
        Assert.Equal(RegulationChangeKind.Repealed, repealed.Kind);
        Assert.Equal("Überholt", repealed.Reason);
        Assert.Equal(["a3"], repealed.PublisherIds);
        Assert.Equal(Since.AddHours(3), newest);
        Assert.Equal(DateTimeKind.Utc, newest!.Value.Kind);
    }

    [Fact]
    public async Task Collector_finds_nothing_after_the_newest_event()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);

        var (changes, newest) = await new RegulationChangeCollector(ctx.Factory).CollectAsync(Since.AddHours(3));

        Assert.Empty(changes);
        Assert.Null(newest);
    }

    [Fact]
    public async Task Signers_take_the_chip_or_the_name_and_the_rank()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);
        await using (var db = ctx.NewContext())
        {
            db.Users.AddRange(
                Seed.Agent("a1", Rank.DeputyDirector, configure: a => a.DiscordId = "111111111111111111"),
                Seed.Agent("a2", Rank.SpecialAgent));
            await db.SaveChangesAsync();
        }
        var collector = new RegulationChangeCollector(ctx.Factory);
        var (changes, _) = await collector.CollectAsync(Since);

        var signers = await collector.SignersAsync(changes);

        Assert.Equal([
            ("<@111111111111111111>", RankDisplay.Name(Rank.DeputyDirector)),
            (DiscordCards.Escape("Snapshot-a2"), RankDisplay.Name(Rank.SpecialAgent)),
            (DiscordCards.Escape("Snapshot-a3"), ""),
        ], signers);
    }

    [Fact]
    public async Task State_round_trips_and_updates()
    {
        using var ctx = new SqliteTestContext();
        var collector = new RegulationChangeCollector(ctx.Factory);

        Assert.Null(await collector.GetStateAsync());
        await collector.SetStateAsync(Since);
        Assert.Equal(Since, await collector.GetStateAsync());
        await collector.SetStateAsync(Since.AddMinutes(5));
        Assert.Equal(Since.AddMinutes(5), await collector.GetStateAsync());
        await using var db = ctx.NewContext();
        Assert.Equal(1, await db.SystemSettings.CountAsync(s => s.Key == SystemSettingKeys.DiscordDvoState));
    }

    // ---------------------------------------------------------------- worker

    private sealed class FakeSender : IDiscordBotSender
    {
        public ConcurrentQueue<(ulong Channel, IReadOnlyList<IMessageComponentProperties> Card, AllowedMentionsProperties Mentions)> Sent { get; } = new();

        public bool Fail { get; set; }

        public Task<ulong> SendAsync(ulong channelId, IReadOnlyList<IMessageComponentProperties> card,
            AllowedMentionsProperties mentions, ulong? replyTo = null, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new HttpRequestException("down");
            }
            Sent.Enqueue((channelId, card, mentions));
            return Task.FromResult((ulong)Sent.Count);
        }

        public Task SendTextAsync(ulong channelId, string content, AllowedMentionsProperties mentions,
            ulong? replyTo = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task EditAsync(ulong channelId, ulong messageId, IReadOnlyList<IMessageComponentProperties> card,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(ulong channelId, ulong messageId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private static ServiceProvider Services(SqliteTestContext ctx, FakeSender sender, ulong? channel = 9)
    {
        var settings = Substitute.For<IDiscordBotSettingsService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(
            DiscordBotSettings.Off with { Enabled = true, DvoChannelId = channel });
        var webhook = Substitute.For<IDiscordWebhookService>();
        webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, BaseUrl,
            new Dictionary<NotificationType, string?>(),
            new Dictionary<NotificationType, string?> { [NotificationType.Announcement] = Role }, false));
        return new ServiceCollection()
            .AddSingleton(settings)
            .AddSingleton(webhook)
            .AddSingleton<IDiscordBotSender>(sender)
            .AddSingleton<IRegulationChangeCollector>(new RegulationChangeCollector(ctx.Factory))
            .BuildServiceProvider();
    }

    [Fact]
    public async Task Worker_starts_from_now_without_a_state()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);
        var sender = new FakeSender();
        await using var services = Services(ctx, sender);
        var now = Since.AddDays(1);

        await DiscordBotWorker.PostRegulationChangesAsync(services, now, CancellationToken.None);

        Assert.Empty(sender.Sent);
        Assert.Equal(now, await services.GetRequiredService<IRegulationChangeCollector>().GetStateAsync());
    }

    [Fact]
    public async Task Worker_waits_for_quiet_then_posts_once_and_pings_the_role_first()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);
        var sender = new FakeSender();
        await using var services = Services(ctx, sender);
        var collector = services.GetRequiredService<IRegulationChangeCollector>();
        await collector.SetStateAsync(Since);
        var newest = Since.AddHours(3);

        await DiscordBotWorker.PostRegulationChangesAsync(services, newest.AddMinutes(14), CancellationToken.None);
        Assert.Empty(sender.Sent);

        await DiscordBotWorker.PostRegulationChangesAsync(services, newest.AddMinutes(15), CancellationToken.None);
        var (channel, card, mentions) = Assert.Single(sender.Sent);
        Assert.Equal(9UL, channel);
        Assert.False(mentions.Everyone);
        Assert.Equal([ulong.Parse(Role)], mentions.AllowedRoles!);
        Assert.Empty(mentions.AllowedUsers!);
        Assert.Contains($"<@&{Role}>", Everything(card));
        Assert.Equal(newest, await collector.GetStateAsync());

        await DiscordBotWorker.PostRegulationChangesAsync(services, newest.AddHours(1), CancellationToken.None);
        Assert.Single(sender.Sent);
    }

    [Fact]
    public async Task Worker_pings_nobody_after_the_first_message()
    {
        using var ctx = new SqliteTestContext();
        await using (var db = ctx.NewContext())
        {
            db.RegulationSections.Add(SectionRow("s1", 10));
            for (var i = 1; i <= 6; i++)
            {
                db.RegulationParagraphs.Add(ParagraphRow($"p{i}", "s1", configure: p => { p.SortOrder = i; p.CurrentVersion = 2; }));
                db.RegulationVersions.AddRange(
                    By(VersionRow($"p{i}", 1, Stamp, $"<p>{new string('a', 1500)}</p>"), "a0"),
                    By(VersionRow($"p{i}", 2, Since.AddMinutes(i), $"<p>{new string('b', 1500)}</p>"), "a1"));
            }
            await db.SaveChangesAsync();
        }
        var sender = new FakeSender();
        await using var services = Services(ctx, sender);
        await services.GetRequiredService<IRegulationChangeCollector>().SetStateAsync(Since);

        await DiscordBotWorker.PostRegulationChangesAsync(services, Since.AddHours(1), CancellationToken.None);

        var sent = sender.Sent.ToList();
        Assert.True(sent.Count >= 2);
        Assert.All(sent.Skip(1), s =>
        {
            Assert.False(s.Mentions.Everyone);
            Assert.Empty(s.Mentions.AllowedRoles!);
            Assert.Empty(s.Mentions.AllowedUsers!);
        });
    }

    [Fact]
    public async Task Worker_keeps_the_state_when_sending_fails()
    {
        using var ctx = new SqliteTestContext();
        await SeedRegulationAsync(ctx);
        var sender = new FakeSender { Fail = true };
        await using var services = Services(ctx, sender);
        var collector = services.GetRequiredService<IRegulationChangeCollector>();
        await collector.SetStateAsync(Since);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            DiscordBotWorker.PostRegulationChangesAsync(services, Since.AddDays(1), CancellationToken.None));

        Assert.Equal(Since, await collector.GetStateAsync());
    }

    [Fact]
    public async Task Worker_is_off_without_a_channel()
    {
        using var ctx = new SqliteTestContext();
        var sender = new FakeSender();
        await using var services = Services(ctx, sender, channel: null);

        await DiscordBotWorker.PostRegulationChangesAsync(services, Since, CancellationToken.None);

        Assert.Null(await services.GetRequiredService<IRegulationChangeCollector>().GetStateAsync());
    }
}
