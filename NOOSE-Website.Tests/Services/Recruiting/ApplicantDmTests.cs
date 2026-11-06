using System.Security.Claims;
using Microsoft.Extensions.Logging;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Infrastructure.Storage;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Recruiting;

/// <summary>Applicant direct messages: one fixed text per step for applicants only, and each recruiting action asks for exactly its own notice.</summary>
public sealed class ApplicantDmTests
{
    private const string DiscordId = "300000000000000001";
    private const string BaseUrl = "https://portal.example";
    private static readonly DateTime Ts = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The words the portal keeps out of every applicant surface, plus the placeholder tokens of the letters.</summary>
    private static readonly string[] Forbidden =
        ["bestanden", "Punkte", "Ergebnis", "Bewertung", "Prozent", "Richtig", "durchgefallen"];

    private static readonly string[] PlaceholderTokens = ["NAME", "BEWERBER", "DATUM", "UHRZEIT", "DIENSTGRAD"];

    public static IEnumerable<object[]> Events() => Enum.GetValues<ApplicantDmEvent>().Select(e => new object[] { e });

    // ---------- the table ----------

    [Theory]
    [InlineData(ApplicantDmEvent.Received, "Deine Bewerbung beim FIB ist eingegangen.", "/portal/status")]
    [InlineData(ApplicantDmEvent.TestAssigned, "Für deine Bewerbung ist ein Test freigegeben.", "/portal/test")]
    [InlineData(ApplicantDmEvent.TestExpired, "Die Bearbeitungszeit deines Tests ist abgelaufen.", "/portal/status")]
    [InlineData(ApplicantDmEvent.NewMessage, "Du hast eine neue Nachricht zu deiner Bewerbung.", "/portal/status")]
    [InlineData(ApplicantDmEvent.StatusChanged, "Es gibt einen neuen Stand zu deiner Bewerbung.", "/portal/status")]
    public void The_table_holds_the_agreed_text_and_route(ApplicantDmEvent e, string text, string route)
    {
        Assert.Equal((text, route), ApplicantDmTexts.For(e));
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void Texts_hold_no_verdict_word_and_no_placeholder(ApplicantDmEvent e)
    {
        var (text, route) = ApplicantDmTexts.For(e);

        Assert.False(string.IsNullOrWhiteSpace(text));
        foreach (var word in Forbidden)
        {
            Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var token in PlaceholderTokens)
        {
            Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        }
        Assert.DoesNotContain(text, c => "{}%$<>@#".Contains(c));
        Assert.StartsWith("/portal/", route);
    }

    [Fact]
    public void Every_event_has_a_text_of_its_own()
    {
        var texts = Enum.GetValues<ApplicantDmEvent>().Select(e => ApplicantDmTexts.For(e).Text).ToList();

        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Theory]
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.ImVorstellungsgespraech, false)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.ImTest, false)]
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.ImTest, false)]
    [InlineData(BewerbungStatus.Eingereicht, BewerbungStatus.InSicherheitspruefung, true)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImTest, true)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImVorstellungsgespraech, true)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.Angenommen, true)]
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.Abgelehnt, true)]
    [InlineData(BewerbungStatus.Abgelehnt, BewerbungStatus.Angenommen, true)]
    public void A_status_change_is_visible_only_when_the_applicant_name_changes(
        BewerbungStatus from, BewerbungStatus to, bool visible)
    {
        Assert.Equal(visible, BewerbungStatusDisplay.ChangesApplicantView(from, to));
    }

    // ---------- the notifier ----------

    private sealed class Notifying : IDisposable
    {
        public SqliteTestContext Ctx { get; } = new();

        public IDiscordDmService Switches { get; } = Substitute.For<IDiscordDmService>();

        public IDiscordWebhookService Webhook { get; } = Substitute.For<IDiscordWebhookService>();

        public DiscordDmQueue Queue { get; } = new();

        public ApplicantDmNotifier Notifier { get; }

        public Notifying(bool on = true)
        {
            Switches.ApplicantsActiveAsync(Arg.Any<CancellationToken>()).Returns(on);
            Webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, BaseUrl,
                new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(),
                IncludeHeadline: true));
            Notifier = new ApplicantDmNotifier(Ctx.Factory, Switches, Webhook, Queue,
                Substitute.For<ILogger<ApplicantDmNotifier>>());
        }

        public void Add(string id = "app1", AgentStatus status = AgentStatus.Applicant, string discordId = DiscordId)
        {
            using var db = Ctx.NewContext();
            db.Users.Add(Seed.Agent(id, status: status, configure: a => a.DiscordId = discordId));
            db.SaveChanges();
        }

        public List<DiscordDmJob> Jobs()
        {
            var jobs = new List<DiscordDmJob>();
            while (Queue.Reader.TryRead(out var job))
            {
                jobs.Add(job);
            }
            return jobs;
        }

        public void Dispose() => Ctx.Dispose();
    }

    [Theory]
    [MemberData(nameof(Events))]
    public async Task Each_event_queues_exactly_one_job_with_the_fixed_text_and_the_absolute_link(ApplicantDmEvent e)
    {
        using var f = new Notifying();
        f.Add();

        await f.Notifier.NotifyAsync("app1", e);

        var job = Assert.Single(f.Jobs());
        var (text, route) = ApplicantDmTexts.For(e);
        Assert.Equal("app1", job.AgentId);
        Assert.Equal(DiscordId, job.DiscordId);
        Assert.Equal($"{text}\n\n{BaseUrl}{route}", job.Content);
        // no channel fallback: the portal bell is the fallback for an applicant
        Assert.Null(job.FallbackType);
        Assert.Null(job.FallbackHref);
        Assert.Null(job.FallbackContent);
    }

    [Fact]
    public async Task The_link_is_joined_onto_the_configured_base_without_a_double_slash()
    {
        using var f = new Notifying();
        f.Webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, BaseUrl + "/",
            new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(),
            IncludeHeadline: true));
        f.Add();

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.TestAssigned);

        Assert.EndsWith($"\n\n{BaseUrl}/portal/test", Assert.Single(f.Jobs()).Content);
    }

    [Fact]
    public async Task A_switched_off_notice_queues_nothing()
    {
        using var f = new Notifying(on: false);
        f.Add();

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.Received);

        Assert.Empty(f.Jobs());
    }

    [Theory]
    [InlineData(AgentStatus.Active)]
    [InlineData(AgentStatus.Pending)]
    [InlineData(AgentStatus.Blocked)]
    [InlineData(AgentStatus.Terminated)]
    [InlineData(AgentStatus.Civilian)]
    public async Task Only_applicants_get_a_notice(AgentStatus status)
    {
        using var f = new Notifying();
        f.Add(status: status);

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.NewMessage);

        Assert.Empty(f.Jobs());
    }

    [Theory]
    [InlineData("")]
    [InlineData("demo")]
    [InlineData("discord-app1")]
    public async Task An_applicant_without_a_Discord_id_gets_nothing(string discordId)
    {
        using var f = new Notifying();
        f.Add(discordId: discordId);

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.NewMessage);

        Assert.Empty(f.Jobs());
    }

    [Fact]
    public async Task An_unknown_account_gets_nothing()
    {
        using var f = new Notifying();

        await f.Notifier.NotifyAsync("nobody", ApplicantDmEvent.StatusChanged);

        Assert.Empty(f.Jobs());
    }

    [Fact]
    public async Task Another_applicant_is_never_addressed()
    {
        using var f = new Notifying();
        f.Add("app1");
        f.Add("app2", discordId: "300000000000000002");

        await f.Notifier.NotifyAsync("app2", ApplicantDmEvent.StatusChanged);

        var job = Assert.Single(f.Jobs());
        Assert.Equal("app2", job.AgentId);
        Assert.Equal("300000000000000002", job.DiscordId);
    }

    [Fact]
    public async Task A_failing_lookup_never_throws_and_queues_nothing()
    {
        using var f = new Notifying();
        f.Add();
        f.Switches.ApplicantsActiveAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new InvalidOperationException("db weg")));

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.Received);

        Assert.Empty(f.Jobs());
    }

    [Fact]
    public async Task A_failing_config_read_never_throws_and_queues_nothing()
    {
        using var f = new Notifying();
        f.Add();
        f.Webhook.GetConfigAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<DiscordWebhookConfig>(new InvalidOperationException("db weg")));

        await f.Notifier.NotifyAsync("app1", ApplicantDmEvent.Received);

        Assert.Empty(f.Jobs());
    }

    // ---------- the wiring: BewerbungService ----------

    private static (BewerbungService Svc, IApplicantDmNotifier Dm) BuildApplications(
        SqliteTestContext ctx, INotificationService? notifications = null)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-B-2026-0001");
        var dm = Substitute.For<IApplicantDmNotifier>();
        var svc = new BewerbungService(ctx.Factory, caseNo, Substitute.For<ISourcesStorageService>(),
            new BewerbungBroadcaster(), Substitute.For<IBewerbungssperreService>(),
            notifications ?? Substitute.For<INotificationService>(), dm, Substitute.For<IApplicationCaseService>(),
            Substitute.For<ILogger<BewerbungService>>());
        return (svc, dm);
    }

    private static ClaimsPrincipal Hrb()
        => ClaimsPrincipalBuilder.Agent("hrb").AsHrb().WithRank(Rank.Trainee).WithCodename("Falcon").Build();

    private static ClaimsPrincipal ApplicantPrincipal(string id = "u1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Applicant).Build();

    private static void SeedApplication(SqliteTestContext ctx, BewerbungStatus status)
    {
        using var db = ctx.NewContext();
        db.Bewerbungen.Add(new Bewerbung
        {
            Id = "b1",
            CaseNumber = "FIB-B-2026-0001",
            ApplicantUserId = "u1",
            Name = "Max Mustermann",
            Status = status,
            SubmittedAt = Ts,
            CreatedAt = Ts,
        });
        db.SaveChanges();
    }

    private static async Task AssertAskedOnlyFor(IApplicantDmNotifier dm, ApplicantDmEvent expected)
    {
        await dm.Received(1).NotifyAsync("u1", expected, Arg.Any<CancellationToken>());
        Assert.Single(dm.ReceivedCalls());
    }

    private static void AssertSilent(IApplicantDmNotifier dm) => Assert.Empty(dm.ReceivedCalls());

    [Fact]
    public async Task Submitting_tells_the_applicant_the_application_arrived()
    {
        using var ctx = new SqliteTestContext();
        var (svc, dm) = BuildApplications(ctx);

        await svc.SubmitAsync(new BewerbungSubmitModel { Name = "Max Mustermann", StreamConsent = true },
            null, null, null, ApplicantPrincipal());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.Received);
    }

    [Fact]
    public async Task A_refused_submission_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.SubmitAsync(
            new BewerbungSubmitModel { Name = "Max Mustermann", StreamConsent = true }, null, null, null,
            ApplicantPrincipal()));

        AssertSilent(dm);
    }

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, BewerbungStatus.InSicherheitspruefung, true)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImTest, true)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, BewerbungStatus.ImVorstellungsgespraech, true)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.Angenommen, true)]
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.Abgelehnt, true)]
    [InlineData(BewerbungStatus.Eingereicht, BewerbungStatus.Geschlossen, true)]
    // the step from the test to the interview must stay invisible, in content and in timing
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.ImVorstellungsgespraech, false)]
    public async Task Setting_a_status_tells_the_applicant_only_when_their_view_changes(
        BewerbungStatus from, BewerbungStatus to, bool tells)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, from);
        var (svc, dm) = BuildApplications(ctx);

        await svc.SetStatusAsync("b1", to, null, Hrb());

        if (tells)
        {
            await AssertAskedOnlyFor(dm, ApplicantDmEvent.StatusChanged);
        }
        else
        {
            AssertSilent(dm);
        }
    }

    [Fact]
    public async Task A_refused_status_change_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SetStatusAsync("b1", BewerbungStatus.Angenommen, null, Hrb()));

        AssertSilent(dm);
    }

    [Fact]
    public async Task The_status_notice_follows_the_save()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);
        BewerbungStatus? stored = null;
        dm.NotifyAsync(Arg.Any<string>(), Arg.Any<ApplicantDmEvent>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                using var db = ctx.NewContext();
                stored = db.Bewerbungen.Single(b => b.Id == "b1").Status;
                return Task.CompletedTask;
            });

        await svc.SetStatusAsync("b1", BewerbungStatus.InSicherheitspruefung, null, Hrb());

        Assert.Equal(BewerbungStatus.InSicherheitspruefung, stored);
    }

    [Fact]
    public async Task A_failing_notifier_cannot_undo_a_status_change()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);
        dm.NotifyAsync(Arg.Any<string>(), Arg.Any<ApplicantDmEvent>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));

        await svc.SetStatusAsync("b1", BewerbungStatus.InSicherheitspruefung, null, Hrb());

        using var db = ctx.NewContext();
        Assert.Equal(BewerbungStatus.InSicherheitspruefung, db.Bewerbungen.Single(b => b.Id == "b1").Status);
    }

    [Theory]
    [InlineData(BewerbungStatus.Abgelehnt, BewerbungStatus.Eingereicht)]
    [InlineData(BewerbungStatus.Abgelehnt, BewerbungStatus.ImTest)]
    [InlineData(BewerbungStatus.Angenommen, BewerbungStatus.ImVorstellungsgespraech)]
    public async Task Reopening_tells_the_applicant_there_is_a_new_state(BewerbungStatus decided, BewerbungStatus target)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, decided);
        var (svc, dm) = BuildApplications(ctx);

        await svc.ReopenAsync("b1", target, "neue Lage", Hrb());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.StatusChanged);
    }

    [Fact]
    public async Task A_refused_reopening_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.ImTest);
        var (svc, dm) = BuildApplications(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ReopenAsync("b1", BewerbungStatus.Eingereicht, null, Hrb()));

        AssertSilent(dm);
    }

    [Theory]
    // passed advances to the test stage: a new name only from before the selection
    [InlineData(BewerbungStatus.InSicherheitspruefung, true, true)]
    [InlineData(BewerbungStatus.Eingereicht, true, true)]
    [InlineData(BewerbungStatus.ImTest, true, false)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, true, false)]
    // failed rejects
    [InlineData(BewerbungStatus.InSicherheitspruefung, false, true)]
    [InlineData(BewerbungStatus.ImTest, false, true)]
    public async Task A_security_result_tells_the_applicant_only_when_their_view_changes(
        BewerbungStatus from, bool passed, bool tells)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, from);
        var (svc, dm) = BuildApplications(ctx);

        await svc.SetSecurityResultAsync("b1", passed, Hrb());

        if (tells)
        {
            await AssertAskedOnlyFor(dm, ApplicantDmEvent.StatusChanged);
        }
        else
        {
            AssertSilent(dm);
        }
    }

    private static int BellRings(INotificationService bell) => bell.ReceivedCalls()
        .Count(c => c.GetMethodInfo().Name == nameof(INotificationService.NotifyAsync)
            && c.GetArguments()[0] as string == "u1" && c.GetArguments()[1] is NotificationType.Recruiting);

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, BewerbungStatus.InSicherheitspruefung, true)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, BewerbungStatus.Angenommen, true)]
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.Abgelehnt, true)]
    // the step from the test to the interview rings nothing, or the applicant could tell it from the bell
    [InlineData(BewerbungStatus.ImTest, BewerbungStatus.ImVorstellungsgespraech, false)]
    public async Task Setting_a_status_rings_the_applicant_bell_only_when_their_view_changes(
        BewerbungStatus from, BewerbungStatus to, bool rings)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, from);
        var bell = Substitute.For<INotificationService>();
        var (svc, _) = BuildApplications(ctx, bell);

        await svc.SetStatusAsync("b1", to, null, Hrb());

        Assert.Equal(rings ? 1 : 0, BellRings(bell));
    }

    [Theory]
    [InlineData(BewerbungStatus.Abgelehnt, BewerbungStatus.ImTest)]
    [InlineData(BewerbungStatus.Angenommen, BewerbungStatus.ImVorstellungsgespraech)]
    public async Task Reopening_rings_the_applicant_bell_once(BewerbungStatus decided, BewerbungStatus target)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, decided);
        var bell = Substitute.For<INotificationService>();
        var (svc, _) = BuildApplications(ctx, bell);

        await svc.ReopenAsync("b1", target, "neue Lage", Hrb());

        Assert.Equal(1, BellRings(bell));
    }

    [Theory]
    [InlineData(BewerbungStatus.InSicherheitspruefung, true, true)]
    [InlineData(BewerbungStatus.ImTest, false, true)]
    // a repeated check inside the selection changes nothing the applicant sees
    [InlineData(BewerbungStatus.ImTest, true, false)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, true, false)]
    public async Task A_security_result_rings_the_applicant_bell_only_when_their_view_changes(
        BewerbungStatus from, bool passed, bool rings)
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, from);
        var bell = Substitute.For<INotificationService>();
        var (svc, _) = BuildApplications(ctx, bell);

        await svc.SetSecurityResultAsync("b1", passed, Hrb());

        Assert.Equal(rings ? 1 : 0, BellRings(bell));
    }

    [Fact]
    public async Task Writing_to_the_applicant_tells_them_about_the_new_message()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);

        await svc.PostToApplicantAsync("b1", "<p>Willkommen beim FIB</p>", Hrb());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.NewMessage);
    }

    [Fact]
    public async Task An_empty_message_to_the_applicant_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.PostToApplicantAsync("b1", "   ", Hrb()));

        AssertSilent(dm);
    }

    [Fact]
    public async Task An_internal_note_and_the_applicants_own_message_tell_the_applicant_nothing()
    {
        using var ctx = new SqliteTestContext();
        SeedApplication(ctx, BewerbungStatus.Eingereicht);
        var (svc, dm) = BuildApplications(ctx);

        await svc.PostInternalAsync("b1", "nur intern", Hrb());
        await svc.PostAsApplicantAsync("b1", "<p>Frage</p>", ApplicantPrincipal());

        AssertSilent(dm);
    }

    // ---------- the wiring: the test services ----------

    private static void SeedAttempt(SqliteTestContext ctx, BewerbungStatus status = BewerbungStatus.ImTest,
        bool assigned = true, DateTime? startedAt = null, DateTime? deadlineAt = null)
    {
        using var db = ctx.NewContext();
        db.BewerbungTests.Add(new BewerbungTest
        {
            Id = "t1", Title = "Test t1", Sorting = 1, TimeLimitMinutes = 30, CreatedAt = Ts,
        });
        db.Bewerbungen.Add(new Bewerbung
        {
            Id = "b1", CaseNumber = "FIB-B-2026-0001", ApplicantUserId = "u1", Name = "Max Mustermann",
            Status = status, SubmittedAt = Ts, CreatedAt = Ts,
        });
        if (assigned)
        {
            db.BewerbungTestAssignments.Add(new BewerbungTestAssignment
            {
                Id = "a1", BewerbungId = "b1", TestId = "t1", StartedAt = startedAt, DeadlineAt = deadlineAt,
                TimeLimitMinutes = startedAt is null ? null : 30, CreatedAt = Ts,
            });
        }
        db.SaveChanges();
    }

    private static (BewerbungTestService Svc, IApplicantDmNotifier Dm) BuildTests(SqliteTestContext ctx)
    {
        var dm = Substitute.For<IApplicantDmNotifier>();
        return (new BewerbungTestService(ctx.Factory, new BewerbungBroadcaster(),
            Substitute.For<INotificationService>(), dm), dm);
    }

    private static (BewerbungTestExpiryService Svc, IApplicantDmNotifier Dm) BuildSweep(SqliteTestContext ctx)
    {
        var dm = Substitute.For<IApplicantDmNotifier>();
        return (new BewerbungTestExpiryService(ctx.Factory, new BewerbungBroadcaster(),
            Substitute.For<INotificationService>(), dm), dm);
    }

    private static ClaimsPrincipal TestHrb()
        => ClaimsPrincipalBuilder.Agent("hrb1").AsHrb().WithCodename("HRB-Alpha").Build();

    [Fact]
    public async Task Assigning_a_test_tells_the_applicant_it_is_released()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, BewerbungStatus.Eingereicht, assigned: false);
        var (svc, dm) = BuildTests(ctx);

        await svc.AssignAsync("b1", "t1", TestHrb());

        // one notice for the release; the move into the test stage does not add a second one
        await AssertAskedOnlyFor(dm, ApplicantDmEvent.TestAssigned);
    }

    [Fact]
    public async Task A_refused_assignment_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx);
        var (svc, dm) = BuildTests(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.AssignAsync("b1", "t1", TestHrb()));

        AssertSilent(dm);
    }

    [Fact]
    public async Task Releasing_the_test_again_tells_the_applicant()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, startedAt: Ts, deadlineAt: Ts.AddMinutes(30));
        var (svc, dm) = BuildTests(ctx);

        await svc.ResetAttemptAsync("b1", null, "Technischer Abbruch", TestHrb());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.TestAssigned);
    }

    [Fact]
    public async Task A_refused_release_tells_nobody()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, BewerbungStatus.Abgelehnt, startedAt: Ts, deadlineAt: Ts.AddMinutes(30));
        var (svc, dm) = BuildTests(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.ResetAttemptAsync("b1", null, "Technischer Abbruch", TestHrb()));

        AssertSilent(dm);
    }

    [Fact]
    public async Task The_read_path_that_closes_an_expired_attempt_tells_the_applicant_once()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddHours(-2));
        var (svc, dm) = BuildTests(ctx);

        await svc.SaveDraftAsync("a1", 1, Array.Empty<TestAnswerInput>(), ApplicantPrincipal());
        await svc.SaveDraftAsync("a1", 1, Array.Empty<TestAnswerInput>(), ApplicantPrincipal());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.TestExpired);
    }

    [Fact]
    public async Task The_sweep_tells_the_applicant_once_when_it_closes_an_expired_attempt()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1));
        var (sweep, dm) = BuildSweep(ctx);

        Assert.Equal(1, await sweep.ExpireDueAsync());
        Assert.Equal(0, await sweep.ExpireDueAsync());

        await AssertAskedOnlyFor(dm, ApplicantDmEvent.TestExpired);
    }

    [Fact]
    public async Task The_sweep_stays_silent_for_a_decided_application()
    {
        using var ctx = new SqliteTestContext();
        SeedAttempt(ctx, BewerbungStatus.Abgelehnt, startedAt: Ts, deadlineAt: DateTime.UtcNow.AddMinutes(-1));
        var (sweep, dm) = BuildSweep(ctx);

        Assert.Equal(0, await sweep.ExpireDueAsync());

        AssertSilent(dm);
    }
}
