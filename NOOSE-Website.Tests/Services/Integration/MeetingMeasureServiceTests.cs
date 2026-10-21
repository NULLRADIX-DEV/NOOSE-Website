using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Meetings;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Measures taken under an agenda item: what they create, who may, and the status the item shows.</summary>
public sealed class MeetingMeasureServiceTests
{
    private const string MeetingId = "b1";
    private const string ItemId = "top1";

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Senior()
        => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static MeetingMeasureService Build(SqliteTestContext ctx)
    {
        // the real case number service runs MySQL-only SQL; distinct numbers keep the unique index happy
        var caseNo = Substitute.For<ICaseNumberService>();
        var counter = 0;
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"NOOSE-A-2026-{++counter:0000}");
        var notifications = Substitute.For<INotificationService>();
        return new MeetingMeasureService(ctx.Factory, new JobService(ctx.Factory, caseNo, notifications),
            new FollowupService(ctx.Factory, notifications));
    }

    /// <summary>A meeting with one item and three selectable agents.</summary>
    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent("senior", Rank.SeniorSpecialAgent));
        db.Users.Add(Seed.Agent("resp", Rank.SpecialAgent));
        db.Meetings.Add(new Meeting
        {
            Id = MeetingId,
            CaseNumber = "NOOSE-BS-2026-0001",
            Title = "Wochenrunde",
            Start = new DateTime(2026, 10, 20, 18, 0, 0, DateTimeKind.Utc),
            Status = MeetingStatus.Planned,
        });
        db.MeetingAgendaItems.Add(new MeetingAgendaItem
        {
            Id = ItemId, MeetingId = MeetingId, Title = "Observation Hafen", Sorting = 10,
        });
        await db.SaveChangesAsync();
        return ctx;
    }

    private static MeetingMeasureInput JobMeasure(string? responsible = "resp") => new()
    {
        Kind = MeetingMeasureKind.Aufgabe,
        Title = "Hafen überwachen",
        Note = "Zwei Schichten am Wochenende",
        ResponsibleAgentId = responsible,
        DueAt = new DateTime(2026, 10, 27),
    };

    private static MeetingMeasureInput FollowupMeasure() => new()
    {
        Kind = MeetingMeasureKind.Wiedervorlage,
        Note = "Ergebnis vorlegen",
        ResponsibleAgentId = "resp",
        DueAt = new DateTime(2026, 11, 3),
    };

    [Fact]
    public async Task AJobMeasureCreatesAnAssignedJobAndShowsItsStatus()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx);

        var row = await svc.CreateAsync(ItemId, JobMeasure(), Leader());

        Assert.Equal(MeetingMeasureKind.Aufgabe, row.Kind);
        Assert.Equal("Hafen überwachen", row.Title);
        Assert.Equal(JobStatusDisplay.Name(JobStatus.Open), row.Status);
        Assert.False(row.Done);
        Assert.Equal("Codename-resp", row.ResponsibleCodename);

        await using var db = ctx.NewContext();
        var job = await db.Jobs.SingleAsync();
        Assert.Equal($"/aufgaben/{job.Id}", row.Href);
        Assert.Equal("resp", (await db.JobAssignments.SingleAsync()).AgentId);
        // the way back to the meeting, and the item it came from
        Assert.Contains(MentionParser.Token(nameof(Meeting), MeetingId), job.Description);
        Assert.Contains("Observation Hafen", job.Description);
        var measure = await db.MeetingMeasures.SingleAsync();
        Assert.Equal((ItemId, job.Id), (measure.AgendaItemId, measure.TargetId));
    }

    [Fact]
    public async Task WithoutAnAssigneeTheJobGoesToWhoeverCreatedIt()
    {
        using var ctx = await SeededAsync();

        await Build(ctx).CreateAsync(ItemId, JobMeasure(responsible: null), Leader());

        await using var db = ctx.NewContext();
        Assert.Equal("lead", (await db.JobAssignments.SingleAsync()).AgentId);
    }

    [Fact]
    public async Task AFollowupMeasureHangsOnTheMeetingAndNamesTheItem()
    {
        // on the item it would drop out of dashboard, calendar and reminder, which resolve a meeting but no item
        using var ctx = await SeededAsync();

        var row = await Build(ctx).CreateAsync(ItemId, FollowupMeasure(), Leader());

        await using var db = ctx.NewContext();
        var followup = await db.Followups.SingleAsync();
        Assert.Equal((nameof(Meeting), MeetingId), (followup.EntityType, followup.EntityId));
        Assert.Equal("resp", followup.ResponsibleAgentId);
        Assert.StartsWith("Punkt „Observation Hafen“", followup.Note);
        Assert.Equal(MeetingMeasureKind.Wiedervorlage, row.Kind);
        Assert.Equal("Offen", row.Status);
        Assert.Equal(followup.Id, (await db.MeetingMeasures.SingleAsync()).TargetId);
    }

    [Fact]
    public async Task OnlyLeadershipWithWriteAccessTakesMeasures()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(ItemId, JobMeasure(), Senior()));
        // the write check runs first, so the supervision never mints a job number
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(ItemId, JobMeasure(), Supervision()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Empty(await db.MeetingMeasures.ToListAsync());
    }

    [Fact]
    public async Task AJobNeedsATitleAndAFollowupADueDate()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx);

        var untitled = JobMeasure();
        untitled.Title = "  ";
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(ItemId, untitled, Leader()));
        var undated = FollowupMeasure();
        undated.DueAt = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(ItemId, undated, Leader()));

        await using var db = ctx.NewContext();
        Assert.Empty(await db.Jobs.ToListAsync());
        Assert.Empty(await db.Followups.ToListAsync());
    }

    [Fact]
    public async Task TheItemShowsTheLiveStatusOfItsMeasures()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx);
        await svc.CreateAsync(ItemId, JobMeasure(), Leader());
        await svc.CreateAsync(ItemId, FollowupMeasure(), Leader());

        await using (var db = ctx.NewContext())
        {
            (await db.Jobs.SingleAsync()).Status = JobStatus.Done;
            (await db.Followups.SingleAsync()).Done = true;
            await db.SaveChangesAsync();
        }

        var measures = (await svc.GetForMeetingAsync(MeetingId, Leader()))[ItemId];

        Assert.Equal(2, measures.Count);
        Assert.All(measures, m => Assert.True(m.Done));
    }

    [Fact]
    public async Task ARestrictedJobStaysHiddenFromWhoeverMayNotSeeIt()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.Jobs.Add(new Job
            {
                Id = "geheim", CaseNumber = "NOOSE-A-2026-0099", Title = "Geheime Aufgabe", IsRestricted = true,
            });
            db.MeetingMeasures.Add(new MeetingMeasure
            {
                AgendaItemId = ItemId, Kind = MeetingMeasureKind.Aufgabe, TargetId = "geheim",
            });
            await db.SaveChangesAsync();
        }
        var svc = Build(ctx);

        // the senior agent reads the agenda, but the job is neither theirs nor open to them
        var forSenior = await svc.GetForMeetingAsync(MeetingId, Senior());
        Assert.Empty(forSenior.GetValueOrDefault(ItemId) ?? []);

        var forLeader = await svc.GetForMeetingAsync(MeetingId, Leader());
        Assert.Single(forLeader[ItemId]);
    }

    [Fact]
    public async Task ADeletedTargetDropsOutOfTheItem()
    {
        using var ctx = await SeededAsync();
        var svc = Build(ctx);
        await svc.CreateAsync(ItemId, JobMeasure(), Leader());

        await using (var db = ctx.NewContext())
        {
            db.Jobs.Remove(await db.Jobs.SingleAsync());
            await db.SaveChangesAsync();
        }

        var measures = await svc.GetForMeetingAsync(MeetingId, Leader());
        Assert.Empty(measures.GetValueOrDefault(ItemId) ?? []);
    }

    [Fact]
    public async Task AnOpenItemTakesItsMeasuresIntoTheNextMeeting()
    {
        using var ctx = await SeededAsync();
        await Build(ctx).CreateAsync(ItemId, JobMeasure(), Leader());
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("NOOSE-BS-2026-0002");
        var meetings = new MeetingService(ctx.Factory, caseNo, Substitute.For<INotificationService>(),
            Substitute.For<ILinkService>());

        var next = await meetings.NextCreateAsync(MeetingId, Leader());

        await using var db = ctx.NewContext();
        var carried = await db.MeetingAgendaItems.SingleAsync(p => p.MeetingId == next.Id);
        var pointers = await db.MeetingMeasures.ToListAsync();
        Assert.Equal(2, pointers.Count);
        // both items point at the same job: the old one keeps its own, the new one asks what came of it
        Assert.Single(pointers.Select(p => p.TargetId).Distinct());
        Assert.Contains(pointers, p => p.AgendaItemId == carried.Id);
    }
}
