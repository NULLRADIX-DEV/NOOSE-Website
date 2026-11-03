using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Authorization;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;
using NOOSE_Website.Tests.Infrastructure;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The leadership key figures of the public area.</summary>
public sealed class PublicKpiServiceTests
{
    private const string PersonId = "p1";
    private const string ProfileId = "b1";

    private static readonly DateTime Now = DateTime.UtcNow;

    private static ClaimsPrincipal Leadership()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("ro").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Plain()
        => ClaimsPrincipalBuilder.Agent("agent").WithRank(Rank.Trainee).Build();

    private static PublicKpiService NewService(SqliteTestContext ctx)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(ctx.Connection).Options;
        return new PublicKpiService(new TestDbContextFactory(options));
    }

    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.People.Add(Seed.Person(PersonId, "Max Mustermann", p => p.CaseNumber = "FIB-P-2026-0001"));
        db.BuergerProfile.Add(new BuergerProfil
        {
            Id = ProfileId, UserId = "u1", FirstName = "Erika", LastName = "Mustermann",
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static Hinweis Tip(TipStatus status, int daysAgo = 1)
        => new()
        {
            Id = Guid.NewGuid().ToString(),
            CaseNumber = "FIB-H-" + Guid.NewGuid().ToString()[..6],
            CitizenProfileId = ProfileId,
            Text = "Ich habe etwas gesehen.",
            Status = status,
            CreatedAt = Now.AddDays(-daysAgo),
        };

    private static async Task AddAsync(SqliteTestContext ctx, params object[] rows)
    {
        await using var db = ctx.NewContext();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    // ---- the guard ----

    [Fact]
    public async Task APlainAgentIsRefused()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(30, Plain()));
    }

    [Fact]
    public async Task TheReadOnlySupervisionMayRead()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        var report = await service.GetAsync(30, Supervision());

        Assert.Equal(30, report.Days);
    }

    // ---- tips ----

    [Fact]
    public async Task TheCaptureRateIsMeasuredAgainstDecidedTipsNotAgainstEverythingReceived()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await AddAsync(ctx,
            Tip(TipStatus.FuehrteZurErgreifung),
            Tip(TipStatus.Verworfen),
            Tip(TipStatus.Neu),
            Tip(TipStatus.InPruefung));

        var tips = (await service.GetAsync(30, Leadership())).Tips;

        Assert.Equal(4, tips.Received);
        Assert.Equal(2, tips.Decided);
        Assert.Equal(2, tips.Open);
        Assert.Equal(1, tips.Captures);
        // an open tip is not a failure
        Assert.Equal(0.5, tips.CaptureShare);
    }

    [Fact]
    public async Task TipsOutsideTheWindowDoNotCount()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await AddAsync(ctx, Tip(TipStatus.Neu, daysAgo: 2), Tip(TipStatus.Neu, daysAgo: 40));

        Assert.Equal(1, (await service.GetAsync(7, Leadership())).Tips.Received);
        Assert.Equal(2, (await service.GetAsync(90, Leadership())).Tips.Received);
    }

    // ---- rewards ----

    [Fact]
    public async Task TheRewardPerCaptureCountsOnlyThePaidOnes()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var tip = Tip(TipStatus.FuehrteZurErgreifung);
        await AddAsync(ctx, tip,
            new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-1", PersonId = PersonId, DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Gefasst, CapturedAt = Now.AddDays(-1),
                PublishedAt = Now.AddDays(-3),
            },
            new OeffentlicheFahndung
            {
                Id = "f2", CaseNumber = "FA-2", PersonId = PersonId, DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Gefasst, CapturedAt = Now.AddDays(-1),
                PublishedAt = Now.AddDays(-3),
            },
            new FahndungKopfgeldAnteil
            {
                Id = "a1", WantedId = "f1", Origin = BountyOrigin.NooseKasse, Amount = 6000m,
                Status = BountyShareStatus.Ausgezahlt, Timestamp = Now.AddDays(-2),
            },
            new HinweisBelohnung
            {
                Id = "r1", ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "a1", Amount = 6000m,
                KassenBuchungId = "k1", PaidAt = Now.AddDays(-1),
            });

        var rewards = (await service.GetAsync(30, Leadership())).Rewards;

        Assert.Equal(6000m, rewards.Paid);
        Assert.Equal(6000m, rewards.FromTill);
        Assert.Equal(0m, rewards.HandedOver);
        Assert.Equal(1, rewards.PaidCaptures);
        Assert.Equal(2, rewards.Captures);
        Assert.Equal(1, rewards.RewardedCaptures);
        // 6000 over the ONE arrest that cost something, not over both
        Assert.Equal(6000m, rewards.PerPaidCapture);
        Assert.Equal(0.5, rewards.RewardedShare);
    }

    [Fact]
    public async Task APayoutForAnOlderArrestDoesNotPushTheShareAboveEverything()
    {
        // the payout cohort and the arrest cohort are different sets: dividing one by the other used to be able to
        // report more rewarded arrests than there were arrests
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var tip = Tip(TipStatus.FuehrteZurErgreifung);
        await AddAsync(ctx, tip,
            new OeffentlicheFahndung
            {
                Id = "old", CaseNumber = "FA-ALT", PersonId = PersonId, DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Gefasst, CapturedAt = Now.AddDays(-200),
                PublishedAt = Now.AddDays(-220),
            },
            new FahndungKopfgeldAnteil
            {
                Id = "a1", WantedId = "old", Origin = BountyOrigin.NooseKasse, Amount = 500m,
                Status = BountyShareStatus.Ausgezahlt, Timestamp = Now.AddDays(-2),
            },
            new HinweisBelohnung
            {
                Id = "r1", ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "a1", Amount = 500m,
                KassenBuchungId = "k1", PaidAt = Now.AddDays(-1),
            });

        var rewards = (await service.GetAsync(30, Leadership())).Rewards;

        Assert.Equal(500m, rewards.Paid);
        Assert.Equal(1, rewards.PaidCaptures);
        // no arrest inside the window, so the share is zero rather than infinite
        Assert.Equal(0, rewards.Captures);
        Assert.Equal(0, rewards.RewardedCaptures);
        Assert.Equal(0, rewards.RewardedShare);
    }

    [Fact]
    public async Task WithoutAnAnsweredTicketTheReactionTimeIsSilentRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Null(tickets.MedianReplyMinutes);
        Assert.Null(tickets.P95ReplyMinutes);
    }

    [Fact]
    public async Task WithoutAPayoutTheCostPerCaptureIsSilentRatherThanZero()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        Assert.Null((await service.GetAsync(30, Leadership())).Rewards.PerPaidCapture);
    }

    // ---- tickets ----

    [Fact]
    public async Task TheAutomaticEntryConfirmationIsNotAnAnswer()
    {
        // it is written in the ticket's own SaveChanges and carries the ticket's timestamp, so a naive measurement
        // reports a perfect desk on every installation with an active template
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddHours(-3);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", CitizenProfileId = ProfileId, Subject = "Anliegen",
                Status = TicketStatus.Offen, CreatedAt = opened, LastActivityAt = opened,
            },
            new TicketNachricht
            {
                Id = "m1", TicketId = "t1", Audience = TicketMessageAudience.Buerger, AuthorIsCitizen = false,
                Text = "Ihr Anliegen ist eingegangen.", CreatedAt = opened,
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(1, tickets.Opened);
        Assert.Equal(0, tickets.Answered);
        Assert.Equal(1, tickets.Waiting);
        Assert.NotNull(tickets.OldestWaitingMinutes);
    }

    [Fact]
    public async Task AHumanReplyIsMeasuredFromTheOpeningOfTheTicket()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddHours(-4);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", CitizenProfileId = ProfileId, Subject = "Anliegen",
                Status = TicketStatus.WartetAufBuerger, CreatedAt = opened, LastActivityAt = Now,
            },
            new TicketNachricht
            {
                Id = "m1", TicketId = "t1", Audience = TicketMessageAudience.Buerger, AuthorIsCitizen = false,
                Text = "Eingang bestätigt.", CreatedAt = opened,
            },
            new TicketNachricht
            {
                Id = "m2", TicketId = "t1", Audience = TicketMessageAudience.Intern, AuthorIsCitizen = false,
                Text = "Interner Vermerk.", CreatedAt = opened.AddHours(1),
            },
            new TicketNachricht
            {
                Id = "m3", TicketId = "t1", Audience = TicketMessageAudience.Buerger, AuthorIsCitizen = false,
                Text = "Wir haben geprüft.", CreatedAt = opened.AddHours(2),
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(1, tickets.Answered);
        Assert.Equal(0, tickets.Waiting);
        // two hours to the first line a human wrote; the internal note is not an answer to the citizen
        Assert.Equal(120, tickets.MedianReplyMinutes);
    }

    [Fact]
    public async Task AnOldUnansweredTicketStillCountsAsWaiting()
    {
        // "still unanswered" is a statement about now, not about the window: windowed, the longest-neglected ticket
        // in the house fell out of the figure and the panel reported an all-clear
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddDays(-400);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", CitizenProfileId = ProfileId, Subject = "Sehr altes Anliegen",
                Status = TicketStatus.Offen, CreatedAt = opened, LastActivityAt = opened,
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        // outside the 30-day cohort, so it is neither opened nor answered here
        Assert.Equal(0, tickets.Opened);
        Assert.Equal(1, tickets.Waiting);
        Assert.True(tickets.OldestWaitingMinutes > 400 * 24 * 60 - 60);
    }

    [Fact]
    public async Task AnAnsweredOpenTicketIsNotWaiting()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddDays(-200);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", CitizenProfileId = ProfileId, Subject = "Beantwortetes Anliegen",
                Status = TicketStatus.WartetAufBuerger, CreatedAt = opened, LastActivityAt = Now,
            },
            new TicketNachricht
            {
                Id = "m1", TicketId = "t1", Audience = TicketMessageAudience.Buerger, AuthorIsCitizen = false,
                Text = "Wir haben geprüft.", CreatedAt = opened.AddHours(3),
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(0, tickets.Waiting);
        Assert.Null(tickets.OldestWaitingMinutes);
    }

    [Fact]
    public async Task AClosedTicketNeverWaits()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddDays(-2);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", CitizenProfileId = ProfileId, Subject = "Erledigt",
                Status = TicketStatus.Geschlossen, CreatedAt = opened, LastActivityAt = Now,
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(1, tickets.Opened);
        Assert.Equal(0, tickets.Waiting);
    }

    [Fact]
    public async Task AnInternalTicketIsNotAPublicFigure()
    {
        // it has no citizen thread at all, so it can never be answered: counted in, it would sit in
        // "Noch ohne Antwort" for ever and drag the reaction times of a desk it never reached
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var opened = Now.AddDays(-2);
        await AddAsync(ctx,
            new Ticket
            {
                Id = "t1", CaseNumber = "FIB-T-1", Kind = TicketArt.Intern, CitizenProfileId = null,
                Subject = "Internes Anliegen", Status = TicketStatus.Offen,
                CreatedAt = opened, LastActivityAt = opened,
            });

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(0, tickets.Opened);
        Assert.Equal(0, tickets.Waiting);
        Assert.Null(tickets.OldestWaitingMinutes);
    }

    // ---- views ----

    [Fact]
    public async Task OnlyNoticesPublishedInTheWindowAreCounted()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await AddAsync(ctx,
            new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-1", PersonId = PersonId, DisplayName = "Im Fenster",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = Now.AddDays(-2), ViewCount = 10,
            },
            new OeffentlicheFahndung
            {
                Id = "f2", CaseNumber = "FA-2", PersonId = PersonId, DisplayName = "Zu alt",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = Now.AddDays(-90), ViewCount = 999,
            },
            new OeffentlicheFahndung
            {
                Id = "f3", CaseNumber = "FA-3", PersonId = PersonId, DisplayName = "Nie draußen",
                Status = PublicWantedStatus.Entwurf, ViewCount = 5,
            });

        var views = (await service.GetAsync(30, Leadership())).Views;

        Assert.NotNull(views);
        Assert.Equal(10, views!.Total);
        Assert.Equal(1, views.Notices);
        Assert.Equal(["FA-1"], views.Top.Select(t => t.CaseNumber));
    }

    [Fact]
    public async Task TheTopListRanksByViewCount()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await AddAsync(ctx,
            new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-1", PersonId = PersonId, DisplayName = "Wenig",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = Now.AddDays(-2), ViewCount = 3,
            },
            new OeffentlicheFahndung
            {
                Id = "f2", CaseNumber = "FA-2", PersonId = PersonId, DisplayName = "Viel",
                Status = PublicWantedStatus.Veroeffentlicht, PublishedAt = Now.AddDays(-2), ViewCount = 40,
            });

        var views = (await service.GetAsync(30, Leadership())).Views;

        Assert.Equal(["FA-2", "FA-1"], views!.Top.Select(t => t.CaseNumber));
        Assert.Equal(43, views.Total);
    }

    [Fact]
    public void TheRecordFilterOnTheAttentionListIsFailClosed_NotALiveBranch()
    {
        // The list names notices, so it runs the same record gate the management list applies. For this panel's
        // audience that gate removes nothing today — leadership and the read-only supervision both read classified
        // records — so it is defence for the day the panel guard widens, not a live filter. Asserted so that day
        // is a red test rather than a leak.
        foreach (var supervision in new[] { false, true })
        {
            var builder = ClaimsPrincipalBuilder.Agent("x").WithRank(Rank.Director);
            if (supervision)
            {
                builder = builder.AsTeamLead();
            }
            var scope = ViewerScope.From(builder.Build());

            Assert.True(RecordVisibility.IsVisible(scope, classified: true, tru: true, hrb: true));
        }
    }

    [Fact]
    public async Task ConfirmedCountsMoreThanJustTheArrests()
    {
        // ConfirmedRows is the wider set; with one row of each the two figures were equal and either predicate
        // could have been swapped for the other
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await AddAsync(ctx,
            Tip(TipStatus.Bestaetigt),
            Tip(TipStatus.Bestaetigt),
            Tip(TipStatus.FuehrteZurErgreifung),
            Tip(TipStatus.Verworfen));

        var tips = (await service.GetAsync(30, Leadership())).Tips;

        Assert.Equal(3, tips.Confirmed);
        Assert.Equal(1, tips.Captures);
        Assert.Equal(4, tips.Decided);
    }

    [Fact]
    public async Task ARewardHandedOverInPersonIsSummedApartFromTheTill()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var tip = Tip(TipStatus.FuehrteZurErgreifung);
        await AddAsync(ctx, tip,
            new FahndungKopfgeldAnteil
            {
                Id = "a1", WantedId = "f1", Origin = BountyOrigin.NooseKasse, Amount = 400m,
                Status = BountyShareStatus.Ausgezahlt, Timestamp = Now.AddDays(-2),
            },
            new FahndungKopfgeldAnteil
            {
                Id = "a2", WantedId = "f1", Origin = BountyOrigin.AgentPrivat, Amount = 600m,
                Status = BountyShareStatus.Ausgezahlt, Timestamp = Now.AddDays(-2),
            },
            new OeffentlicheFahndung
            {
                Id = "f1", CaseNumber = "FA-1", PersonId = PersonId, DisplayName = "Max Mustermann",
                Status = PublicWantedStatus.Gefasst, CapturedAt = Now.AddDays(-1), PublishedAt = Now.AddDays(-3),
            },
            new HinweisBelohnung
            {
                Id = "r1", ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "a1", Amount = 400m,
                KassenBuchungId = "k1", PaidAt = Now.AddDays(-1),
            },
            new HinweisBelohnung
            {
                Id = "r2", ReceiptNumber = "BEL-1", TipId = tip.Id, ShareId = "a2", Amount = 600m,
                SelfPaidAt = Now.AddDays(-1), PaidAt = Now.AddDays(-1),
            });

        var rewards = (await service.GetAsync(30, Leadership())).Rewards;

        Assert.Equal(1000m, rewards.Paid);
        Assert.Equal(400m, rewards.FromTill);
        Assert.Equal(600m, rewards.HandedOver);
        // one notice, so the cost per paid arrest is the whole sum
        Assert.Equal(1, rewards.PaidCaptures);
        Assert.Equal(1000m, rewards.PerPaidCapture);
    }

    [Fact]
    public async Task TheReactionTimePercentilesAreRealPercentilesNotTheOnlySample()
    {
        // three answered tickets at 1 h, 2 h and 10 h: the median is the middle one and p95 the slowest, so
        // neither can be the mean, the minimum or the single-sample shortcut
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var rows = new List<object>();
        var hours = new[] { 1, 2, 10 };
        for (var i = 0; i < hours.Length; i++)
        {
            var opened = Now.AddHours(-20);
            rows.Add(new Ticket
            {
                Id = "t" + i, CaseNumber = "FIB-T-" + i, CitizenProfileId = ProfileId, Subject = "Anliegen",
                Status = TicketStatus.WartetAufBuerger, CreatedAt = opened, LastActivityAt = Now,
            });
            rows.Add(new TicketNachricht
            {
                Id = "m" + i, TicketId = "t" + i, Audience = TicketMessageAudience.Buerger,
                AuthorIsCitizen = false, Text = "Antwort", CreatedAt = opened.AddHours(hours[i]),
            });
        }
        await AddAsync(ctx, [.. rows]);

        var tickets = (await service.GetAsync(30, Leadership())).Tickets;

        Assert.Equal(3, tickets.Answered);
        Assert.Equal(120, tickets.MedianReplyMinutes);
        Assert.Equal(600, tickets.P95ReplyMinutes);
    }

    [Fact]
    public void TheViewGateIsTheFailClosedPath_NotALiveBranch()
    {
        // Views is nullable because the attention list names notices and therefore answers to the notice-list
        // audience, not to the panel's own. Today the two coincide — leadership implies the rank, and the read-only
        // supervision passes both — so the null branch cannot be reached. Asserted rather than assumed: the day
        // someone widens the panel guard, this goes red before the cross-list leaks.
        foreach (var rank in Enum.GetValues<Rank>())
        {
            foreach (var supervision in new[] { false, true })
            {
                var builder = ClaimsPrincipalBuilder.Agent("x").WithRank(rank);
                if (supervision)
                {
                    builder = builder.AsTeamLead();
                }
                var user = builder.Build();
                if (!user.MayClassifiedRead())
                {
                    continue;
                }
                Assert.True(user.MayHighestClassification() || user.IsOnlyReader(),
                    $"Rang {rank} (Aufsicht: {supervision}) liest die Auswertung, aber nicht die Ausschreibungsliste.");
            }
        }
    }
}
