using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Chat;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Members are pinged once per unread run of the taskforce chat.</summary>
public sealed class TaskforceChatPingTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private const string MemberId = "11111111-1111-1111-1111-111111111111";

    private static ClaimsPrincipal Author()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal Member()
        => ClaimsPrincipalBuilder.Agent(MemberId).WithRank(Rank.Trainee).WithCodename("Rookie").Build();

    private static void Seed(SqliteTestContext ctx, DateTime? readAt = null, bool archived = false,
        params TaskforceMessage[] messages)
    {
        using var db = ctx.NewContext();
        db.Users.Add(NOOSE_Website.Tests.Infrastructure.Seed.Agent("lead", Rank.Director));
        db.Users.Add(NOOSE_Website.Tests.Infrastructure.Seed.Agent(MemberId, Rank.Trainee));
        db.Taskforces.Add(new Taskforce
        {
            Id = "tf",
            Name = "Nordwind",
            CaseNumber = "FIB-TF-2026-0001",
            IsArchived = archived,
            CreatedAt = Now.AddDays(-30),
        });
        db.TaskforceAgents.Add(new TaskforceAgent { TaskforceId = "tf", AgentId = "lead", CreatedAt = Now.AddDays(-10) });
        db.TaskforceAgents.Add(new TaskforceAgent
        {
            TaskforceId = "tf", AgentId = MemberId, CreatedAt = Now.AddDays(-10), LastReadAt = readAt,
        });
        db.TaskforceMessages.AddRange(messages);
        db.SaveChanges();
    }

    private static TaskforceMessage Message(DateTime at, string by)
        => new() { TaskforceId = "tf", Text = "…", AuthorName = "Wolf", CreatedAt = at, CreatedById = by };

    private static Task ReceivedPing(INotificationService notifications, int times)
        => notifications.Received(times).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(ids => ids.SequenceEqual(new[] { MemberId })),
            NotificationType.TaskforceChat, Arg.Is<string>(t => t.Contains("Nordwind")), "/taskforces/tf", "lead",
            Arg.Any<CancellationToken>());

    [Fact]
    public async Task The_first_unread_message_pings_the_member()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, readAt: Now.AddMinutes(-5));
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .SendAsync("tf", "Lagebesprechung um acht", Author());

        await ReceivedPing(notifications, 1);
    }

    [Fact]
    public async Task While_something_is_still_unread_no_second_ping_goes_out()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, Now.AddHours(-2), false, Message(Now.AddHours(-1), "lead"));
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .SendAsync("tf", "Noch was", Author());

        await notifications.DidNotReceive().NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.TaskforceChat, Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Own_messages_do_not_hold_the_next_ping_back()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, Now.AddHours(-2), false, Message(Now.AddHours(-1), MemberId));
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .SendAsync("tf", "Antwort", Author());

        await ReceivedPing(notifications, 1);
    }

    [Fact]
    public async Task Whoever_has_the_chat_open_is_not_pinged()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, readAt: Now.AddMinutes(-5));
        var notifications = Substitute.For<INotificationService>();
        var broadcaster = new TaskforceChatBroadcaster();
        broadcaster.Enter("tf", MemberId);

        await new TaskforceChatService(ctx.Factory, broadcaster, notifications).SendAsync("tf", "Hallo", Author());

        await ReceivedPing(notifications, 0);

        // closing the last tab counts again
        broadcaster.Leave("tf", MemberId);
        Assert.False(broadcaster.IsWatching("tf", MemberId));
    }

    [Fact]
    public async Task A_mentioned_member_gets_the_mention_only()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, readAt: Now.AddMinutes(-5));
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .SendAsync("tf", $"@{{Agent:{MemberId}}} schau mal", Author());

        await ReceivedPing(notifications, 0);
    }

    [Fact]
    public async Task An_archived_taskforce_pings_nobody()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx, readAt: Now.AddMinutes(-5), archived: true);
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .SendAsync("tf", "Hallo", Author());

        await ReceivedPing(notifications, 0);
    }

    [Fact]
    public async Task Opening_the_chat_reads_its_bell_notice()
    {
        using var ctx = new SqliteTestContext();
        Seed(ctx);
        var notifications = Substitute.For<INotificationService>();

        await new TaskforceChatService(ctx.Factory, new TaskforceChatBroadcaster(), notifications)
            .MarkReadAsync("tf", Member());

        await notifications.Received(1).TargetAsReadMarkAsync(Arg.Any<ClaimsPrincipal>(),
            NotificationType.TaskforceChat, "/taskforces/tf", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }
}
