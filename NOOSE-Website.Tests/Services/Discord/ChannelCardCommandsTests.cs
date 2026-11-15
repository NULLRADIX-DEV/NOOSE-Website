using System.Security.Claims;
using NetCord.Rest;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Notifications;
using NOOSE_Website.Infrastructure.Discord;
using NOOSE_Website.Infrastructure.Notifications;
using NOOSE_Website.Models.Calendar;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Discord.Bot;
using NOOSE_Website.Services.Public;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Discord;

/// <summary>The buttons of channel cards: private answers, the clicker's rights, and „Details“ limited to the clicker's own bell.</summary>
public sealed class ChannelCardCommandsTests
{
    private const string Nothing = "Für dich gibt es dazu nichts.";
    private const string JobTitle = "Dir wurde eine Aufgabe zugewiesen: „Observation“";
    private static readonly int Assigned = (int)NotificationType.JobAssigned;

    // ---------------------------------------------------------------- mode

    [Theory]
    [InlineData("cjd")]
    [InlineData("cack")]
    [InlineData("cdates")]
    [InlineData("ctip")]
    [InlineData("ctk")]
    [InlineData("cbw")]
    [InlineData("nd")]
    public void New_actions_answer_privately(string action)
        => Assert.Equal(BotAnswerMode.Reply, DiscordBotRouter.ModeForComponent(action, []));

    // ---------------------------------------------------------------- details

    [Fact]
    public async Task Details_shows_own_notification_and_marks_it_read()
    {
        using var host = new Host();
        var id = host.AddNotice("a1", NotificationType.JobAssigned, JobTitle, "/aufgaben/j1");

        var reply = await host.Router.ComponentAsync("nd", [Assigned.ToString(), "j1"], [], Call("a1"));

        Assert.False(reply.IsError);
        Assert.Contains("Observation", TextOf(reply));
        Assert.NotNull(host.Row(id).ReadAt);
    }

    [Fact]
    public async Task Details_for_a_stranger_is_empty()
    {
        using var host = new Host();
        var id = host.AddNotice("a1", NotificationType.JobAssigned, JobTitle, "/aufgaben/j1");

        var reply = await host.Router.ComponentAsync("nd", [Assigned.ToString(), "j1"], [], Call("a2"));

        Assert.Contains(Nothing, TextOf(reply));
        Assert.DoesNotContain("Observation", TextOf(reply));
        // the click of a stranger touches nothing of the owner's
        Assert.Null(host.Row(id).ReadAt);
    }

    [Fact]
    public async Task Details_takes_the_newest_row_of_the_record_and_ignores_query_and_fragment()
    {
        using var host = new Host();
        var older = new DateTime(2026, 11, 14, 8, 0, 0, DateTimeKind.Utc);
        host.AddNotice("a1", NotificationType.JobAssigned, "Alt", "/aufgaben/j1?tab=1", older);
        var newest = host.AddNotice("a1", NotificationType.JobAssigned, "Neu", "/aufgaben/j1#kommentare", older.AddHours(2));
        // another record, another type
        host.AddNotice("a1", NotificationType.JobAssigned, "Fremde Akte", "/aufgaben/j10", older.AddHours(3));
        host.AddNotice("a1", NotificationType.JobDueSoon, "Fremder Typ", "/aufgaben/j1", older.AddHours(4));

        var reply = await host.Router.ComponentAsync("nd", [Assigned.ToString(), "j1"], [], Call("a1"));

        var text = TextOf(reply);
        Assert.Contains("Neu", text);
        Assert.DoesNotContain("Alt", text);
        Assert.DoesNotContain("Fremde Akte", text);
        Assert.DoesNotContain("Fremder Typ", text);
        // only the shown row is read
        Assert.NotNull(host.Row(newest).ReadAt);
        Assert.Equal(3, host.Unread("a1"));
    }

    [Fact]
    public async Task Details_escapes_the_title_and_offers_a_link_to_the_record()
    {
        using var host = new Host();
        host.AddNotice("a1", NotificationType.JobAssigned, "**fett** @everyone", "/aufgaben/j1");

        var reply = await host.Router.ComponentAsync("nd", [Assigned.ToString(), "j1"], [], Call("a1"));

        Assert.DoesNotContain("**fett**", TextOf(reply));
        Assert.Contains("\\@everyone", TextOf(reply));
        var link = Assert.Single(LinksOf(reply));
        Assert.Equal("https://fib.test/aufgaben/j1", link.Url);
        Assert.Equal("Öffnen", link.Label);
    }

    [Fact]
    public async Task Details_of_an_absolute_href_offers_no_link()
    {
        using var host = new Host();
        host.AddNotice("a1", NotificationType.JobAssigned, JobTitle, "https://elsewhere.test/aufgaben/j1");

        var reply = await host.Router.ComponentAsync("nd", [Assigned.ToString(), "j1"], [], Call("a1"));

        Assert.Contains("Observation", TextOf(reply));
        Assert.Empty(LinksOf(reply));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("99999")]
    public async Task Details_of_an_unknown_type_is_empty(string type)
    {
        using var host = new Host();
        var id = host.AddNotice("a1", NotificationType.JobAssigned, JobTitle, "/aufgaben/j1");

        var reply = await host.Router.ComponentAsync("nd", [type, "j1"], [], Call("a1"));

        Assert.Contains(Nothing, TextOf(reply));
        Assert.Null(host.Row(id).ReadAt);
    }

    // ---------------------------------------------------------------- job

    [Fact]
    public async Task Job_done_from_channel_card_replies_privately()
    {
        using var host = new Host();
        host.AddJob("j1", "creator", assignee: "a1");

        var reply = await host.Router.ComponentAsync("cjd", ["j1"], [], Call("a1"));

        Assert.False(reply.IsError);
        // a short confirmation, never the clicker's task list
        Assert.Equal(TextOf(BotReply.Done("Aufgabe erledigt.")), TextOf(reply));
        using var check = host.Ctx.NewContext();
        Assert.Equal(JobStatus.Done, check.Jobs.Single(j => j.Id == "j1").Status);
    }

    [Fact]
    public async Task Job_done_on_someone_elses_job_is_refused_with_the_services_text()
    {
        using var host = new Host();
        host.AddJob("j2", "creator", assignee: null);

        var reply = await host.Router.ComponentAsync("cjd", ["j2"], [], Call("a2"));

        Assert.True(reply.IsError);
        Assert.Contains("Den Status darf nur ein Beteiligter", TextOf(reply));
        using var check = host.Ctx.NewContext();
        Assert.Equal(JobStatus.Open, check.Jobs.Single(j => j.Id == "j2").Status);
    }

    // ---------------------------------------------------------------- acknowledge and take over

    [Theory]
    [InlineData("cack", "Zur Kenntnis genommen.")]
    [InlineData("ctip", "Du hast den Hinweis übernommen.")]
    [InlineData("ctk", "Du hast das Ticket übernommen.")]
    [InlineData("cbw", "Du hast die Bewerbung übernommen.")]
    public async Task These_buttons_run_as_the_clicker_and_confirm_privately(string action, string text)
    {
        using var host = new Host();
        var call = Call("a1");

        var reply = await host.Router.ComponentAsync(action, ["rec-1"], [], call);

        Assert.False(reply.IsError);
        Assert.Equal(TextOf(BotReply.Done(text)), TextOf(reply));
        host.AssertCalled(action, "rec-1", call.User);
    }

    [Theory]
    [InlineData("cack")]
    [InlineData("ctip")]
    [InlineData("ctk")]
    [InlineData("cbw")]
    public async Task Refusals_of_the_service_become_a_private_error(string action)
    {
        using var host = new Host();
        foreach (Exception refusal in new Exception[] { new UnauthorizedAccessException("Dafür fehlen dir die Rechte."), new InvalidOperationException("Gibt es nicht mehr.") })
        {
            host.Refuse(action, refusal);

            var reply = await host.Router.ComponentAsync(action, ["rec-1"], [], Call("a1"));

            Assert.True(reply.IsError);
            Assert.Contains(refusal.Message, TextOf(reply));
        }
    }

    [Fact]
    public async Task Other_failures_are_left_to_the_handler()
    {
        using var host = new Host();
        host.Refuse("cbw", new TimeoutException());

        await Assert.ThrowsAsync<TimeoutException>(() => host.Router.ComponentAsync("cbw", ["rec-1"], [], Call("a1")));
    }

    // ---------------------------------------------------------------- dates

    [Fact]
    public async Task Dates_from_a_channel_card_show_only_the_clickers_own_appointments()
    {
        using var host = new Host();
        var call = Call("a1");

        var reply = await host.Router.ComponentAsync("cdates", [], [], call);

        Assert.False(reply.IsError);
        Assert.Contains("Deine Termine", TextOf(reply));
        await host.Calendar.Received(1).GetEntriesAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), call.User, CalendarMode.My,
            Arg.Any<CancellationToken>());
    }

    // ---------------------------------------------------------------- helpers

    private static BotCall Call(string agentId) => new()
    {
        Context = new BotContext(300000000000000001, "tester", 100000000000000001, 5),
        Actor = new DiscordActor(Seed.Agent(agentId), ClaimsPrincipalBuilder.Agent(agentId).WithRank(Rank.Trainee).Build()),
        Options = new BotOptions(new Dictionary<string, string>()),
    };

    private static string TextOf(BotReply reply) => string.Concat(reply.Components.OfType<ComponentContainerProperties>()
        .SelectMany(c => c.Components).OfType<TextDisplayProperties>().Select(t => t.Content + "\n"));

    private static IEnumerable<LinkButtonProperties> LinksOf(BotReply reply) => reply.Components.OfType<ComponentContainerProperties>()
        .SelectMany(c => c.Components).OfType<ActionRowProperties>().SelectMany(r => r.Components).OfType<LinkButtonProperties>();

    private sealed class Host : IDisposable
    {
        public SqliteTestContext Ctx { get; } = new();
        public IAnnouncementService Announcements { get; } = Substitute.For<IAnnouncementService>();
        public ITipService Tips { get; } = Substitute.For<ITipService>();
        public ITicketService Tickets { get; } = Substitute.For<ITicketService>();
        public IBewerbungService Applications { get; } = Substitute.For<IBewerbungService>();
        public ICalendarService Calendar { get; } = Substitute.For<ICalendarService>();
        public DiscordBotRouter Router { get; }

        public Host()
        {
            var webhook = Substitute.For<IDiscordWebhookService>();
            webhook.GetConfigAsync(Arg.Any<CancellationToken>()).Returns(new DiscordWebhookConfig(true, "https://fib.test",
                new Dictionary<NotificationType, string?>(), new Dictionary<NotificationType, string?>(), false));
            Calendar.GetEntriesAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CalendarMode>(),
                Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CalendarEntry>>([]));
            var records = new BotRecords(Substitute.For<ISearchService>(), webhook, Substitute.For<IAccessLogService>(), Ctx.Factory);
            var jobs = new JobService(Ctx.Factory, Substitute.For<ICaseNumberService>(), Substitute.For<INotificationService>());
            var notifications = new NotificationService(Ctx.Factory, new NotificationBroadcaster(), Substitute.For<IDiscordWebhookService>(),
                Substitute.For<IDiscordDmService>(), Substitute.For<IDiscordDmQueue>());
            var cards = new ChannelCardCommands(records, jobs, Announcements, Tips, Tickets, Applications, notifications);
            var personal = new PersonalCommands(records, jobs, Substitute.For<IFollowupService>(), Announcements,
                Substitute.For<IRegulationService>(), Calendar, Substitute.For<IAbsenceService>(), Substitute.For<IEmergencyAlarmService>(),
                TimeProvider.System);
            // the modules these buttons never reach stay empty
            Router = new DiscordBotRouter(null!, null!, personal, null!, null!, cards, Substitute.For<ITestLockService>());
        }

        public void Dispose() => Ctx.Dispose();

        public string AddNotice(string recipient, NotificationType type, string title, string href, DateTime? at = null)
        {
            using var db = Ctx.NewContext();
            var row = new Notification
            {
                RecipientId = recipient,
                Type = type,
                Title = title,
                Href = href,
                CreatedAt = at ?? new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc),
            };
            db.Notifications.Add(row);
            db.SaveChanges();
            return row.Id;
        }

        public Notification Row(string id)
        {
            using var db = Ctx.NewContext();
            return db.Notifications.Single(n => n.Id == id);
        }

        public int Unread(string recipient)
        {
            using var db = Ctx.NewContext();
            return db.Notifications.Count(n => n.RecipientId == recipient && n.ReadAt == null);
        }

        public void AddJob(string id, string creator, string? assignee)
        {
            using var db = Ctx.NewContext();
            db.Jobs.Add(new Job
            {
                Id = id,
                Title = "Observation",
                CaseNumber = $"FIB-A-2026-{id}",
                CreatedById = creator,
                CreatedAt = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc),
                Status = JobStatus.Open,
                Priority = JobPriority.Normal,
            });
            if (assignee is not null)
            {
                db.JobAssignments.Add(new JobAssignment { JobId = id, AgentId = assignee });
            }
            db.SaveChanges();
        }

        public void Refuse(string action, Exception failure)
        {
            var failed = Task.FromException(failure);
            switch (action)
            {
                case "cack":
                    Announcements.AcknowledgeAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(failed);
                    break;
                case "ctip":
                    Tips.AssignSelfAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(failed);
                    break;
                case "ctk":
                    Tickets.AssignSelfAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(failed);
                    break;
                default:
                    Applications.AssignSelfAsync(Arg.Any<string>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(failed);
                    break;
            }
        }

        public void AssertCalled(string action, string id, ClaimsPrincipal user)
        {
            switch (action)
            {
                case "cack":
                    Announcements.Received(1).AcknowledgeAsync(id, user, Arg.Any<CancellationToken>());
                    break;
                case "ctip":
                    Tips.Received(1).AssignSelfAsync(id, user, Arg.Any<CancellationToken>());
                    break;
                case "ctk":
                    Tickets.Received(1).AssignSelfAsync(id, user, Arg.Any<CancellationToken>());
                    break;
                default:
                    Applications.Received(1).AssignSelfAsync(id, user, Arg.Any<CancellationToken>());
                    break;
            }
        }
    }
}
