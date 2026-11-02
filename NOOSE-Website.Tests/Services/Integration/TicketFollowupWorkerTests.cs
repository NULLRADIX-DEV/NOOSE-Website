using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Tickets;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The automatic follow-up: one reminder, then a closure that names no agent.</summary>
public sealed class TicketFollowupWorkerTests
{
    private static readonly DateTime Now = new(2026, 9, 20, 12, 0, 0, DateTimeKind.Utc);

    private const string ProfileId = "profil1";
    private const string UserId = "buerger1";

    private static async Task<SqliteTestContext> SeededAsync(TimeSpan idle,
        TicketStatus status = TicketStatus.WartetAufBuerger, DateTime? nudgedAt = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId,
            UserId = UserId,
            FirstName = "Mara",
            LastName = "Klein",
        });
        db.Tickets.Add(new Ticket
        {
            Id = "t1",
            CaseNumber = "FIB-T-2026-0001",
            Subject = "Beschädigtes Fahrzeug",
            CitizenProfileId = ProfileId,
            Status = status,
            LastActivityAt = Now - idle,
            NudgedAt = nudgedAt,
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static async Task<Ticket> RunAsync(SqliteTestContext ctx, INotificationService notifications)
    {
        await using (var db = ctx.NewContext())
        {
            await TicketFollowupWorker.SweepAsync(db, notifications, Now, CancellationToken.None);
        }
        await using var check = ctx.NewContext();
        return check.Tickets.Single(t => t.Id == "t1");
    }

    [Fact]
    public async Task AFreshlyWaitingTicketIsLeftAlone()
    {
        using var ctx = await SeededAsync(TicketRules.NudgeAfter - TimeSpan.FromHours(1));
        var notifications = Substitute.For<INotificationService>();

        var row = await RunAsync(ctx, notifications);

        Assert.Null(row.NudgedAt);
        Assert.Equal(TicketStatus.WartetAufBuerger, row.Status);
        await notifications.DidNotReceive().NotifyOnceAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ItRemindsOnceAndThenStaysQuiet()
    {
        using var ctx = await SeededAsync(TicketRules.NudgeAfter + TimeSpan.FromHours(1));
        var notifications = Substitute.For<INotificationService>();

        var first = await RunAsync(ctx, notifications);
        Assert.Equal(Now, first.NudgedAt);
        // reminding is not movement on the ticket, or it would keep resetting its own deadline
        Assert.NotEqual(Now, first.LastActivityAt);
        Assert.Equal(TicketStatus.WartetAufBuerger, first.Status);
        await notifications.Received(1).NotifyOnceAsync(UserId, NotificationType.PublicTicketAnswered,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // the second pass over the same row must not ring again
        notifications.ClearReceivedCalls();
        await RunAsync(ctx, notifications);
        await notifications.DidNotReceive().NotifyOnceAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithoutAnAnswerItClosesAsNoContactAndNamesNoAgent()
    {
        using var ctx = await SeededAsync(TicketRules.AutoCloseAfter + TimeSpan.FromHours(1), nudgedAt: Now.AddDays(-4));
        var notifications = Substitute.For<INotificationService>();

        var row = await RunAsync(ctx, notifications);

        Assert.Equal(TicketStatus.Geschlossen, row.Status);
        Assert.Equal(TicketAbschlussgrund.KeinKontakt, row.ClosingReason);
        Assert.Equal(Now, row.ClosedAt);
        // a system closure borrows nobody's name
        Assert.Null(row.ClosedById);
        Assert.Null(row.ClosingNote);
        Assert.Null(row.NudgedAt);
        await notifications.Received(1).NotifyOnceAsync(UserId, NotificationType.PublicTicketAnswered,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ATicketNobodyIsWaitingOnIsNeverTouched()
    {
        // the sweep is about the citizen's silence; a ticket in handling is the desk's own business
        using var ctx = await SeededAsync(TicketRules.AutoCloseAfter + TimeSpan.FromDays(30),
            status: TicketStatus.InBearbeitung);
        var notifications = Substitute.For<INotificationService>();

        var row = await RunAsync(ctx, notifications);

        Assert.Equal(TicketStatus.InBearbeitung, row.Status);
        Assert.Null(row.NudgedAt);
    }

    [Fact]
    public async Task AClosedTicketIsNotClosedAgain()
    {
        using var ctx = await SeededAsync(TicketRules.AutoCloseAfter + TimeSpan.FromDays(5),
            status: TicketStatus.Geschlossen);
        var notifications = Substitute.For<INotificationService>();

        var row = await RunAsync(ctx, notifications);

        Assert.Null(row.ClosingReason);
        await notifications.DidNotReceive().NotifyOnceAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheCloseDeadlineIsTheLaterOne()
    {
        Assert.True(TicketRules.NudgeAfter > TimeSpan.Zero);
        Assert.True(TicketRules.NudgeAfter < TicketRules.AutoCloseAfter);
    }
}
