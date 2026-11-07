using System.Data.Common;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Directives;
using NOOSE_Website.Infrastructure.Announcements;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Directives;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="DirectiveService"/> over in-memory SQLite.</summary>
public sealed class DirectiveServiceTests
{
    private const string StaleText = "Inzwischen hat jemand eine neuere Fassung veröffentlicht. Lade die Seite neu.";
    private static readonly DateTime Stamp = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = MeetingTime.Day(DateTime.UtcNow);

    private static (DirectiveService Svc, INotificationService Notifications, List<string> Reported) Build(
        SqliteTestContext ctx, params IInterceptor[] interceptors)
    {
        var options = interceptors.Length == 0
            ? ctx.Options
            : new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).AddInterceptors(interceptors).Options;
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new AcknowledgmentBroadcaster();
        var reported = new List<string>();
        broadcaster.Modified += id => reported.Add(id);
        var svc = new DirectiveService(new TestDbContextFactory(options), notifications, broadcaster,
            NullLogger<DirectiveService>.Instance);
        return (svc, notifications, reported);
    }

    private static ClaimsPrincipal Boss(string id = "boss")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Falke").Build();

    private static ClaimsPrincipal Plain(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("sup").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director).AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static DirectiveInput Input(string title = "Waffenmitführung",
        DocumentClassification level = DocumentClassification.None, string html = "<p>Eins zwei drei.</p>")
        => new(title, "Dienstbetrieb", level, html, Today.AddDays(-1), "Erstfassung");

    private static DirectiveVersionInput Next(int expected, DocumentClassification level = DocumentClassification.None,
        string html = "<p>Eins zwei vier.</p>", bool requires = true, string? reason = null, DateOnly? validFrom = null)
        => new("Waffenmitführung", "Dienstbetrieb", level, html, validFrom ?? Today, "Neufassung", requires, expected, reason);

    private static Directive Row(string id, int number, Action<Directive>? configure = null)
    {
        var directive = new Directive
        {
            Id = id,
            Number = number,
            Title = "Anweisung " + id,
            CurrentVersion = 1,
            AcknowledgeFromVersion = 1,
            CreatedAt = Stamp,
        };
        configure?.Invoke(directive);
        return directive;
    }

    private static async Task SeedAgentsAsync(SqliteTestContext ctx, params Agent[] agents)
    {
        await using var db = ctx.NewContext();
        db.Users.AddRange(agents);
        await db.SaveChangesAsync();
    }

    private static async Task<List<string>> OwingAsync(SqliteTestContext ctx, string directiveId)
    {
        await using var db = ctx.NewContext();
        return (await DirectiveDebt.Owed(db).Where(r => r.DirectiveId == directiveId).Select(r => r.AgentId)
            .ToListAsync()).Order().ToList();
    }

    private static Task RingsNobodyAsync(INotificationService notifications)
        => notifications.DidNotReceive().NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());

    // ---------- CreateAsync ----------

    [Fact]
    public async Task Create_by_leadership_takes_number_one_and_version_one()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _, _) = Build(ctx);

        var id = await svc.CreateAsync(Input(), Boss());

        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(id, head.Id);
        Assert.Equal(1, head.Number);
        Assert.Equal(1, head.CurrentVersion);
        Assert.Equal(1, head.AcknowledgeFromVersion);
        Assert.Equal("Waffenmitführung", head.Title);
        Assert.Equal("Dienstbetrieb", head.Subject);
        Assert.Equal(DocumentClassification.None, head.Classification);
        var version = await db.DirectiveVersions.SingleAsync();
        Assert.Equal(id, version.DirectiveId);
        Assert.Equal(1, version.Version);
        Assert.True(version.RequiresAcknowledgment);
        Assert.Equal("Falke", version.PublishedByName);
        Assert.Equal("boss", version.PublishedById);
        Assert.Equal(Today.AddDays(-1), version.ValidFrom);
        Assert.Contains("Eins zwei drei.", version.ContentHtml);
        Assert.Equal("Erstfassung", version.ChangeNote);
    }

    [Fact]
    public async Task Create_below_leadership_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var sac = ClaimsPrincipalBuilder.Agent("sac").WithRank(Rank.SpecialAgentInCharge).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input(), sac));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.Directives.ToListAsync());
    }

    [Fact]
    public async Task Create_by_supervision_or_demo_is_refused_before_anything_is_written()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        ClaimsPrincipal[] refused =
        [
            Supervision(),
            ClaimsPrincipalBuilder.Agent("demo").WithRank(Rank.Director).AsDemo().Build(),
            Partner(),
        ];

        foreach (var actor in refused)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input(), actor));
        }

        await using var db = ctx.NewContext();
        Assert.Empty(await db.Directives.ToListAsync());
        Assert.Empty(await db.DirectiveVersions.ToListAsync());
        Assert.Empty(await db.DirectiveAcknowledgments.ToListAsync());
    }

    [Theory]
    [InlineData("<p><br></p>")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_with_an_empty_text_is_refused(string html)
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(Input(html: html), Boss()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.Directives.ToListAsync());
    }

    [Fact]
    public async Task Create_refuses_an_overlong_title_and_a_missing_change_note()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        async Task RefusedAsync(DirectiveInput input)
            => await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, Boss()));

        await RefusedAsync(Input() with { Title = new string('T', DirectiveRules.MaxTitle + 1) });
        await RefusedAsync(Input() with { Title = "  " });
        await RefusedAsync(Input() with { Subject = new string('S', DirectiveRules.MaxSubject + 1) });
        await RefusedAsync(Input() with { ChangeNote = "" });
        await RefusedAsync(Input() with { ChangeNote = new string('N', DirectiveRules.MaxChangeNote + 1) });
        await using (var db = ctx.NewContext())
        {
            Assert.Empty(await db.Directives.ToListAsync());
        }

        // the limits themselves are fine
        await svc.CreateAsync(Input() with
        {
            Title = new string('T', DirectiveRules.MaxTitle),
            Subject = new string('S', DirectiveRules.MaxSubject),
            ChangeNote = new string('N', DirectiveRules.MaxChangeNote),
        }, Boss());
        await using var check = ctx.NewContext();
        Assert.Single(await check.Directives.ToListAsync());
    }

    [Fact]
    public async Task A_new_directive_takes_the_next_number_past_trashed_ones()
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.AddRange(
                Row("trashed", 1, d => { d.IsDeleted = true; d.DeletedAt = Stamp; }),
                Row("repealed", 2, d => { d.RepealedAt = Stamp; d.RepealReason = "überholt"; }));
            await seed.SaveChangesAsync();
        }
        var (svc, _, _) = Build(ctx);

        var id = await svc.CreateAsync(Input(), Boss());

        await using var db = ctx.NewContext();
        Assert.Equal(3, (await db.Directives.SingleAsync(d => d.Id == id)).Number);
    }

    [Fact]
    public async Task Create_tries_once_more_when_the_number_was_taken_meanwhile()
    {
        using var ctx = new SqliteTestContext();
        var rival = new RivalNumber(ctx);
        var (svc, _, _) = Build(ctx, rival);

        var id = await svc.CreateAsync(Input(), Boss());

        Assert.True(rival.Fired);
        await using var db = ctx.NewContext();
        Assert.Equal([1, 2], await db.Directives.OrderBy(d => d.Number).Select(d => d.Number).ToListAsync());
        Assert.Equal(2, (await db.Directives.SingleAsync(d => d.Id == id)).Number);
        // nothing is left of the failed attempt
        Assert.Equal(id, (await db.DirectiveVersions.SingleAsync()).DirectiveId);
        Assert.Equal(id, (await db.DirectiveAcknowledgments.SingleAsync()).DirectiveId);
    }

    [Fact]
    public async Task The_publisher_does_not_owe_his_own_version()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);

        var id = await svc.CreateAsync(Input(), Boss());

        Assert.Equal(["a"], await OwingAsync(ctx, id));
        Assert.Equal(0, await svc.GetOpenCountAsync(Boss()));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));

        // a version that asks again leaves its publisher out as well
        await svc.PublishVersionAsync(id, Next(1), Boss());
        Assert.Equal(["a"], await OwingAsync(ctx, id));
        Assert.Equal(0, await svc.GetOpenCountAsync(Boss()));
    }

    // ---------- bell and live badge ----------

    [Fact]
    public async Task Publishing_rings_every_owing_agent_but_the_publisher()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx,
            Seed.Agent("boss", Rank.Director),
            Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.SpecialAgent),
            Seed.Agent("chief", Rank.SectionChief));
        var (svc, notifications, reported) = Build(ctx);

        var id = await svc.CreateAsync(Input(), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "a", "b", "chief" })),
            NotificationType.DirectiveIssued, Arg.Any<string>(), $"/dienstanweisungen/{id}", "boss",
            Arg.Any<CancellationToken>());
        Assert.Equal(["a", "b", "chief"], reported.Order().ToList());

        // a new version that asks again rings the same agents
        notifications.ClearReceivedCalls();
        await svc.PublishVersionAsync(id, Next(1), Boss());
        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "a", "b", "chief" })),
            NotificationType.DirectiveIssued, "Neue Fassung von DA-01", $"/dienstanweisungen/{id}", "boss",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Publishing_a_classified_directive_rings_only_cleared_agents()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx,
            Seed.Agent("boss", Rank.Director),
            Seed.Agent("tru", Rank.SpecialAgent, configure: a => a.IsTRU = true),
            Seed.Agent("hrb", Rank.SpecialAgent, configure: a => a.IsHRB = true),
            Seed.Agent("trainee", Rank.Trainee),
            Seed.Agent("chief", Rank.SectionChief));
        var (svc, notifications, _) = Build(ctx);

        await svc.CreateAsync(Input(level: DocumentClassification.Tru), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Order().SequenceEqual(new[] { "chief", "tru" })),
            NotificationType.DirectiveIssued, Arg.Any<string>(), Arg.Any<string?>(), "boss",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_bell_title_names_only_the_number()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, notifications, _) = Build(ctx);

        await svc.CreateAsync(Input(title: "Geheimer Titel"), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.DirectiveIssued, "Neue Dienstanweisung DA-01", Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<NotificationType>(), Arg.Is<string>(t => t.Contains("Geheimer")), Arg.Any<string?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failing_bell_does_not_undo_the_directive_and_still_reports_the_badge()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, notifications, reported) = Build(ctx);
        notifications.NotifyManyInAppAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<NotificationType>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("Glocke defekt")));

        var id = await svc.CreateAsync(Input(), Boss());

        await using var db = ctx.NewContext();
        Assert.True(await db.Directives.AnyAsync(d => d.Id == id));
        Assert.Equal(["a"], reported);
    }

    // ---------- PublishVersionAsync ----------

    [Fact]
    public async Task A_new_version_with_acknowledgment_reopens_the_debt_for_everyone()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));
        Assert.Equal(["b"], await OwingAsync(ctx, id));

        await svc.PublishVersionAsync(id, Next(1), Boss());

        Assert.Equal(["a", "b"], await OwingAsync(ctx, id));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));
        var open = Assert.Single(await svc.GetOpenForMeAsync(Plain("a")));
        Assert.Equal(2, open.Version);
        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(2, head.CurrentVersion);
        Assert.Equal(2, head.AcknowledgeFromVersion);
    }

    [Fact]
    public async Task A_typo_version_keeps_every_earlier_acknowledgment_valid_and_rings_nobody()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.Trainee));
        var (svc, notifications, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));
        notifications.ClearReceivedCalls();

        await svc.PublishVersionAsync(id, Next(1, requires: false), Boss());

        await RingsNobodyAsync(notifications);
        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(2, head.CurrentVersion);
        Assert.Equal(1, head.AcknowledgeFromVersion);
        Assert.False((await db.DirectiveVersions.SingleAsync(v => v.Version == 2)).RequiresAcknowledgment);
        // a is still square, b still owes the first version
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("b")));
        Assert.Equal(1, Assert.Single(await svc.GetOpenForMeAsync(Plain("b"))).Version);
    }

    [Fact]
    public async Task A_typo_version_that_widens_the_level_rings_only_the_newly_cleared()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("trainee", Rank.Trainee));
        var (svc, notifications, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: DocumentClassification.Leadership), Boss());
        Assert.Equal(["chief"], await OwingAsync(ctx, id));
        notifications.ClearReceivedCalls();
        reported.Clear();

        await svc.PublishVersionAsync(id,
            Next(1, DocumentClassification.None, requires: false, reason: "Fuer alle Agenten geoeffnet"), Boss());

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.SequenceEqual(new[] { "trainee" })),
            NotificationType.DirectiveIssued, "Neue Fassung von DA-01", $"/dienstanweisungen/{id}", "boss",
            Arg.Any<CancellationToken>());
        // the old and the new debtors recount
        Assert.Equal(["chief", "trainee"], reported.Order().ToList());
        Assert.Equal(["chief", "trainee"], await OwingAsync(ctx, id));
        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(DocumentClassification.None, head.Classification);
        Assert.Equal(1, head.AcknowledgeFromVersion);
    }

    [Fact]
    public async Task Loosening_the_level_needs_a_reason()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: DocumentClassification.Tru), Boss());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, DocumentClassification.None), Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, DocumentClassification.None, reason: "   "), Boss()));
        await using (var before = ctx.NewContext())
        {
            // nothing moved
            Assert.Equal(1, (await before.Directives.SingleAsync()).CurrentVersion);
            Assert.Equal(DocumentClassification.Tru, (await before.Directives.SingleAsync()).Classification);
            Assert.Empty(await before.AuditLogs.ToListAsync());
        }

        await svc.PublishVersionAsync(id, Next(1, DocumentClassification.None, reason: "Ganze Behoerde soll es lesen"), Boss());

        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(2, head.CurrentVersion);
        Assert.Equal(DocumentClassification.None, head.Classification);
        var audit = await db.AuditLogs.SingleAsync(a => a.EntityType == nameof(Directive));
        Assert.Equal(id, audit.EntityId);
        Assert.Equal(AuditAction.Justified, audit.Action);
        Assert.Contains(CriticalChange.SecrecyField, audit.ChangesJson);
        Assert.Contains("Ganze Behoerde soll es lesen", audit.ChangesJson);
    }

    [Fact]
    public async Task Tightening_the_level_needs_no_reason_and_ends_the_debt_of_the_uncleared()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("trainee", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        Assert.Equal(["chief", "trainee"], await OwingAsync(ctx, id));
        reported.Clear();

        await svc.PublishVersionAsync(id, Next(1, DocumentClassification.Leadership), Boss());

        Assert.Equal(["chief"], await OwingAsync(ctx, id));
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("trainee")));
        Assert.Contains("trainee", reported);
        await using var db = ctx.NewContext();
        Assert.Empty(await db.AuditLogs.Where(a => a.EntityType == nameof(Directive)).ToListAsync());
    }

    [Fact]
    public async Task Managing_by_a_team_lead_with_admin_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var supervisor = ClaimsPrincipalBuilder.Agent("tl").WithRank(Rank.Director).AsTeamLead().AsAdmin().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(Input(), supervisor));
        var id = await svc.CreateAsync(Input(), Boss());
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.PublishVersionAsync(id, Next(1), supervisor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepealAsync(id, "überholt", supervisor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(id, supervisor));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync(id, supervisor));

        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.Equal(1, head.CurrentVersion);
        Assert.Null(head.RepealedAt);
    }

    [Fact]
    public async Task Publishing_on_a_stale_version_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss("lead-1"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, html: "<p>Anderer Text.</p>"), Boss("lead-2")));

        Assert.Equal(StaleText, ex.Message);
        await using var db = ctx.NewContext();
        Assert.Equal([1, 2], await db.DirectiveVersions.OrderBy(v => v.Version).Select(v => v.Version).ToListAsync());
        Assert.Equal(2, (await db.Directives.SingleAsync()).CurrentVersion);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_repeal_or_trash_between_load_and_publish_makes_the_publish_match_no_row(bool repeal)
    {
        // the publisher's head was loaded before the other leader committed
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.Add(Row("d1", 1));
            await seed.SaveChangesAsync();
        }
        await using var publisher = ctx.NewContext();
        var stale = await publisher.Directives.SingleAsync(d => d.Id == "d1");

        await using (var other = ctx.NewContext())
        {
            var head = await other.Directives.SingleAsync(d => d.Id == "d1");
            if (repeal)
                head.RepealedAt = Stamp;
            else
                head.IsDeleted = true;
            await other.SaveChangesAsync();
        }

        stale.CurrentVersion = 2;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => publisher.SaveChangesAsync());
    }

    [Fact]
    public async Task A_version_dated_before_its_predecessor_is_refused()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.PublishVersionAsync(id, Next(1, validFrom: Today.AddDays(-2)), Boss()));
        // the same day and later are fine
        await svc.PublishVersionAsync(id, Next(1, validFrom: Today.AddDays(-1)), Boss());
        await svc.PublishVersionAsync(id, Next(2, validFrom: Today.AddDays(30)), Boss());

        await using var db = ctx.NewContext();
        Assert.Equal(3, (await db.Directives.SingleAsync()).CurrentVersion);
    }

    [Fact]
    public async Task A_repealed_directive_takes_no_new_version_and_no_acknowledgment()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.RepealAsync(id, "überholt", Boss());

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishVersionAsync(id, Next(1), Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 1, Plain("a")));

        await using var db = ctx.NewContext();
        Assert.Single(await db.DirectiveVersions.ToListAsync());
        // the publisher's own row only
        Assert.Equal("boss", (await db.DirectiveAcknowledgments.SingleAsync()).AgentId);
    }

    [Fact]
    public async Task Publishing_a_hidden_or_missing_directive_reads_as_not_found()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PublishVersionAsync("gibt-es-nicht", Next(1), Boss()));

        Assert.Equal("Dienstanweisung nicht gefunden.", ex.Message);
    }

    // ---------- AcknowledgeAsync ----------

    [Fact]
    public async Task Acknowledge_records_the_newest_version_and_reports_the_agent()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        reported.Clear();

        await svc.AcknowledgeAsync(id, 1, Plain("me"));

        Assert.Equal(["me"], reported);
        await using var db = ctx.NewContext();
        var mine = await db.DirectiveAcknowledgments.SingleAsync(a => a.AgentId == "me");
        Assert.Equal((id, 1), (mine.DirectiveId, mine.Version));
        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("me")));
    }

    [Fact]
    public async Task Acknowledging_a_version_that_is_no_longer_current_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss());
        reported.Clear();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 1, Plain("me")));

        Assert.Equal("Inzwischen gibt es eine neuere Fassung. Lies sie und nimm sie dann zur Kenntnis.", ex.Message);
        Assert.Empty(reported);
        await using var db = ctx.NewContext();
        Assert.False(await db.DirectiveAcknowledgments.AnyAsync(a => a.AgentId == "me"));
        // a version that does not exist yet is no better
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 3, Plain("me")));
    }

    [Fact]
    public async Task Acknowledge_twice_is_harmless()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        reported.Clear();

        await svc.AcknowledgeAsync(id, 1, Plain("me"));
        await svc.AcknowledgeAsync(id, 1, Plain("me"));

        await using var db = ctx.NewContext();
        Assert.Single(await db.DirectiveAcknowledgments.Where(a => a.AgentId == "me").ToListAsync());
        Assert.Equal(["me"], reported);
    }

    [Fact]
    public async Task Acknowledge_by_supervision_or_partner_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Supervision()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Partner()));

        await using var db = ctx.NewContext();
        // the publisher's own row only
        Assert.Single(await db.DirectiveAcknowledgments.ToListAsync());
    }

    [Fact]
    public async Task Acknowledge_checks_the_account_in_the_database()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director),
            Seed.Agent("gone", Rank.Trainee, AgentStatus.Terminated));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());

        // the session still carries the old claims
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AcknowledgeAsync(id, 1, Plain("gone")));

        await using var db = ctx.NewContext();
        Assert.False(await db.DirectiveAcknowledgments.AnyAsync(a => a.AgentId == "gone"));
    }

    [Fact]
    public async Task Acknowledge_a_directive_the_agent_cannot_see_is_refused()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("me", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: DocumentClassification.Tru), Boss());
        reported.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AcknowledgeAsync(id, 1, Plain("me")));

        Assert.Empty(reported);
        await using var db = ctx.NewContext();
        Assert.False(await db.DirectiveAcknowledgments.AnyAsync(a => a.AgentId == "me"));
    }

    [Fact]
    public async Task A_future_version_shows_its_predecessor_as_valid_but_takes_the_acknowledgment()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("me", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1, html: "<p>Neuer Text ab nächster Woche.</p>", validFrom: Today.AddDays(7)), Boss());

        var shown = await svc.GetDetailAsync(id, null, Plain("me"));

        Assert.NotNull(shown);
        Assert.Equal(1, shown.ShownVersion);
        Assert.Contains("Eins zwei drei.", shown.ShownHtml);
        Assert.Equal(1, shown.Row.ValidVersion);
        Assert.Equal(2, shown.Row.CurrentVersion);
        Assert.Equal(Today.AddDays(7), shown.Row.ValidFrom);
        Assert.Equal([2, 1], shown.Versions.Select(v => v.Version));
        Assert.True(shown.MustAcknowledge);

        // the newest version is the one to acknowledge, before its date
        await svc.AcknowledgeAsync(id, 2, Plain("me"));

        var after = await svc.GetDetailAsync(id, 2, Plain("me"));
        Assert.NotNull(after);
        Assert.Equal(2, after.ShownVersion);
        Assert.Contains("Neuer Text", after.ShownHtml);
        Assert.False(after.MustAcknowledge);
        Assert.Equal(2, after.MyAcknowledgedVersion);
        Assert.NotNull(after.MyAcknowledgedAt);
        Assert.Null(await svc.GetDetailAsync(id, 9, Plain("me")));
    }

    // ---------- RepealAsync / DeleteAsync / RestoreAsync ----------

    [Fact]
    public async Task Repeal_needs_a_reason_and_ends_every_debt()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, reported) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        reported.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealAsync(id, "", Boss()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealAsync(id, "   ", Boss()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RepealAsync(id, "überholt", Plain("a")));
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));

        await svc.RepealAsync(id, "  Durch DA-02 ersetzt  ", Boss());

        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        await using var db = ctx.NewContext();
        var head = await db.Directives.SingleAsync();
        Assert.NotNull(head.RepealedAt);
        Assert.Equal("Durch DA-02 ersetzt", head.RepealReason);
        Assert.Equal("boss", head.RepealedById);
        Assert.Equal("Falke", head.RepealedByName);
        Assert.Empty(await svc.GetListAsync(Plain("a"), repealed: false));
        var repealed = Assert.Single(await svc.GetListAsync(Plain("a"), repealed: true));
        Assert.True(repealed.IsRepealed);
        Assert.False(repealed.MustAcknowledge);
        var detail = await svc.GetDetailAsync(id, null, Plain("a"));
        Assert.Equal("Durch DA-02 ersetzt", detail?.RepealReason);
        Assert.Equal("Falke", detail?.RepealedByName);
        // once is enough
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RepealAsync(id, "nochmal", Boss()));
    }

    [Fact]
    public async Task Delete_and_restore_move_the_debt_away_and_back()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, reported) = Build(ctx, new AuditSaveChangesInterceptor(new FixedUser()));
        var id = await svc.CreateAsync(Input(), Boss());
        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync(id, Plain("a")));
        reported.Clear();

        await svc.DeleteAsync(id, Boss());

        Assert.Equal(0, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        Assert.Empty(await svc.GetListAsync(Boss(), repealed: false));
        var trash = Assert.Single(await svc.GetTrashAsync());
        Assert.Equal(id, trash.Id);
        Assert.True(trash.IsDeleted);
        reported.Clear();

        await svc.RestoreAsync(id, Boss());

        Assert.Equal(1, await svc.GetOpenCountAsync(Plain("a")));
        Assert.Equal(["a"], reported);
        Assert.Empty(await svc.GetTrashAsync());
        Assert.Single(await svc.GetListAsync(Boss(), repealed: false));
        // nothing to restore any more
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.RestoreAsync(id, Boss()));
    }

    [Theory]
    [InlineData("repeal")]
    [InlineData("delete")]
    [InlineData("restore")]
    public async Task A_write_on_a_stale_head_reads_as_a_newer_version(string action)
    {
        using var ctx = new SqliteTestContext();
        await using (var seed = ctx.NewContext())
        {
            seed.Directives.Add(Row("d1", 1, d => { d.IsDeleted = action == "restore"; }));
            await seed.SaveChangesAsync();
        }
        var rival = new RivalPublish();
        var (svc, _, _) = Build(ctx, new AuditSaveChangesInterceptor(new FixedUser()), rival);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => action switch
        {
            "repeal" => svc.RepealAsync("d1", "überholt", Boss()),
            "delete" => svc.DeleteAsync("d1", Boss()),
            _ => svc.RestoreAsync("d1", Boss()),
        });

        Assert.True(rival.Fired);
        Assert.Equal(StaleText, ex.Message);
        await using var db = ctx.NewContext();
        var head = await db.Directives.IgnoreQueryFilters().SingleAsync();
        Assert.Null(head.RepealedAt);
        Assert.Equal(action == "restore", head.IsDeleted);
    }

    // ---------- reads ----------

    [Fact]
    public async Task The_detail_reads_the_debt_and_the_rights_of_the_viewer()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());

        var asBoss = await svc.GetDetailAsync(id, null, Boss());
        var asAgent = await svc.GetDetailAsync(id, null, Plain("a"));
        var asSupervision = await svc.GetDetailAsync(id, null, Supervision());

        Assert.NotNull(asBoss);
        Assert.True(asBoss.MayManage);
        Assert.True(asBoss.MaySeeQuota);
        Assert.False(asBoss.MustAcknowledge);
        Assert.Equal(1, asBoss.MyAcknowledgedVersion);
        Assert.NotNull(asAgent);
        Assert.False(asAgent.MayManage);
        Assert.False(asAgent.MaySeeQuota);
        Assert.True(asAgent.MustAcknowledge);
        Assert.True(asAgent.Row.MustAcknowledge);
        Assert.Null(asAgent.MyAcknowledgedVersion);
        Assert.Equal("DA-01", asAgent.Row.Label);
        Assert.Equal(1, asAgent.Row.ValidVersion);
        Assert.NotNull(asSupervision);
        Assert.False(asSupervision.MayManage);
        Assert.True(asSupervision.MaySeeQuota);
        Assert.False(asSupervision.MustAcknowledge);
    }

    [Fact]
    public async Task Quota_is_hidden_from_plain_agents_and_shown_to_supervision()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee),
            Seed.Agent("b", Rank.Trainee),
            Seed.Agent("sup", Rank.Director, configure: a => a.IsTeamLead = true));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.AcknowledgeAsync(id, 1, Plain("a"));

        Assert.Null(await svc.GetQuotaAsync(id, Plain("a")));
        var quota = await svc.GetQuotaAsync(id, Supervision());

        Assert.NotNull(quota);
        // supervision is never in the audience
        Assert.Equal(3, quota.Total);
        Assert.Equal(2, quota.Acknowledged);
        Assert.Equal(1, quota.AcknowledgeFromVersion);
        Assert.Equal("b", Assert.Single(quota.Missing).AgentId);
        Assert.Null(quota.Missing[0].AcknowledgedVersion);
        Assert.Equal(["a", "boss"], quota.Done.Select(d => d.AgentId).Order());
        Assert.All(quota.Done, d => Assert.Equal(1, d.AcknowledgedVersion));
        Assert.All(quota.Done, d => Assert.NotNull(d.AcknowledgedAt));

        // a new version that asks again: the old acknowledgment shows as the highest own version
        await svc.PublishVersionAsync(id, Next(1), Boss());
        var after = await svc.GetQuotaAsync(id, Boss());
        Assert.NotNull(after);
        Assert.Equal(2, after.AcknowledgeFromVersion);
        Assert.Equal(["a", "b"], after.Missing.Select(m => m.AgentId));
        Assert.Equal(1, after.Missing.Single(m => m.AgentId == "a").AcknowledgedVersion);
        Assert.Equal(["boss"], after.Done.Select(d => d.AgentId));
        Assert.Null(await svc.GetQuotaAsync("gibt-es-nicht", Boss()));
    }

    [Fact]
    public async Task Quota_of_a_classified_directive_lists_only_the_cleared_audience()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("chief", Rank.SectionChief),
            Seed.Agent("trainee", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: DocumentClassification.Leadership), Boss());

        var quota = await svc.GetQuotaAsync(id, Supervision());

        Assert.NotNull(quota);
        Assert.Equal(["chief"], quota.Missing.Select(m => m.AgentId));
        Assert.Equal(2, quota.Total);
        Assert.Null(await svc.GetQuotaAsync(id, Plain("trainee")));
    }

    [Fact]
    public async Task Compare_marks_the_changed_words()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());
        await svc.PublishVersionAsync(id, Next(1), Boss());

        // either order reads old to new
        var comparison = await svc.CompareAsync(id, 2, 1, Plain("a"));

        Assert.NotNull(comparison);
        Assert.Equal(1, comparison.FromVersion);
        Assert.Equal(2, comparison.ToVersion);
        Assert.True(comparison.Diff.Added > 0);
        Assert.Contains("noosei-neu", comparison.Diff.Html);
        Assert.Null(await svc.CompareAsync(id, 1, 5, Plain("a")));
        Assert.Null(await svc.CompareAsync("gibt-es-nicht", 1, 2, Plain("a")));
    }

    [Fact]
    public async Task Compare_of_a_hidden_directive_is_null()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(level: DocumentClassification.Tru), Boss());
        await svc.PublishVersionAsync(id, Next(1, DocumentClassification.Tru), Boss());

        Assert.Null(await svc.CompareAsync(id, 1, 2, Plain("a")));
        Assert.NotNull(await svc.CompareAsync(id, 1, 2, Boss()));
    }

    [Fact]
    public async Task GetList_and_detail_are_empty_for_a_partner()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director));
        var (svc, _, _) = Build(ctx);
        var id = await svc.CreateAsync(Input(), Boss());

        Assert.Empty(await svc.GetListAsync(Partner(), repealed: false));
        Assert.Empty(await svc.GetListAsync(Partner(), repealed: true));
        Assert.Null(await svc.GetDetailAsync(id, null, Partner()));
        Assert.Null(await svc.GetQuotaAsync(id, Partner()));
        Assert.Null(await svc.CompareAsync(id, 1, 1, Partner()));
        Assert.Empty(await svc.SearchAsync(null, Partner()));
        Assert.Equal(0, await svc.GetOpenCountAsync(Partner()));
        Assert.Empty(await svc.GetOpenForMeAsync(Partner()));
    }

    [Fact]
    public async Task The_list_runs_by_number_and_hides_what_the_reader_may_not_see()
    {
        using var ctx = new SqliteTestContext();
        await SeedAgentsAsync(ctx, Seed.Agent("boss", Rank.Director), Seed.Agent("a", Rank.Trainee));
        var (svc, _, _) = Build(ctx);
        await svc.CreateAsync(Input("Erste"), Boss());
        await svc.CreateAsync(Input("Geheime", DocumentClassification.Leadership), Boss());
        await svc.CreateAsync(Input("Dritte"), Boss());

        var asAgent = await svc.GetListAsync(Plain("a"), repealed: false);
        var asBoss = await svc.GetListAsync(Boss(), repealed: false);

        Assert.Equal(["DA-01", "DA-03"], asAgent.Select(r => r.Label));
        Assert.Equal(["DA-01", "DA-02", "DA-03"], asBoss.Select(r => r.Label));
        Assert.All(asAgent, r => Assert.True(r.MustAcknowledge));
        Assert.All(asBoss, r => Assert.False(r.MustAcknowledge));
        Assert.All(asBoss, r => Assert.Equal(Today.AddDays(-1), r.ValidFrom));
    }

    [Fact]
    public async Task Search_finds_by_number_and_title_within_visibility()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _) = Build(ctx);
        await svc.CreateAsync(Input("Waffenmitführung"), Boss());
        await svc.CreateAsync(Input("Funkdisziplin"), Boss());
        await svc.CreateAsync(Input("Geheimer Funk", DocumentClassification.Leadership), Boss());

        var byNumber = Assert.Single(await svc.SearchAsync("DA-02", Plain("a")));
        Assert.Equal("DA-02", byNumber.Label);
        Assert.Equal("Funkdisziplin", byNumber.Title);
        Assert.Equal("DA-02", Assert.Single(await svc.SearchAsync("Funkd", Plain("a"))).Label);
        Assert.Equal(["DA-01", "DA-02"], (await svc.SearchAsync(null, Plain("a"))).Select(p => p.Label));
        Assert.Empty(await svc.SearchAsync("DA-03", Plain("a")));
        Assert.Equal(["DA-02", "DA-03"], (await svc.SearchAsync("Funk", Boss())).Select(p => p.Label));
        Assert.Single(await svc.SearchAsync(null, Boss(), take: 1));
    }

    // ---------- doubles ----------

    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("boss", "Falke", true, false, false);
    }

    /// <summary>Takes number 1 in another context right before the service's first save, as a parallel creator would.</summary>
    private sealed class RivalNumber(SqliteTestContext ctx) : SaveChangesInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Fired)
            {
                Fired = true;
                using var rival = ctx.NewContext();
                rival.Directives.Add(Row("rival", 1));
                rival.SaveChanges();
            }
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Bumps the head's version right before the service's own update runs, as a parallel publisher would.</summary>
    private sealed class RivalPublish : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Bump(command);
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Bump(command);
            return ValueTask.FromResult(result);
        }

        private void Bump(DbCommand command)
        {
            if (Fired || !command.CommandText.StartsWith("UPDATE \"Dienstanweisungen\"", StringComparison.Ordinal))
            {
                return;
            }
            Fired = true;
            // same connection, rolls back with the save
            using var rival = command.Connection!.CreateCommand();
            rival.Transaction = command.Transaction;
            rival.CommandText = "UPDATE \"Dienstanweisungen\" SET \"AktuelleFassung\" = \"AktuelleFassung\" + 1";
            rival.ExecuteNonQuery();
        }
    }
}
