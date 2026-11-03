using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Meetings;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="MeetingService" /> against in-memory SQLite.</summary>
public sealed class MeetingServiceTests
{
    private const string ItemType = nameof(MeetingAgendaItem);

    private static (MeetingService Svc, ICaseNumberService CaseNo, INotificationService Notifications, ILinkService Links)
        Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        // real one uses MySQL-only raw SQL; stub it. No test creates two meetings via the
        // service, and seeded meetings use distinct "BS-<id>" numbers, so this cannot collide
        // with the unique index on Meeting.CaseNumber.
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-BS-2026-0001");
        var notifications = Substitute.For<INotificationService>();
        var links = Substitute.For<ILinkService>();
        var svc = new MeetingService(ctx.Factory, caseNo, notifications, links);
        return (svc, caseNo, notifications, links);
    }

    // admin => passes RequireMeetingWrite, RequireLeadership and RequireWriteAccess; agent id "lead"
    private static ClaimsPrincipal WriteActor() =>
        ClaimsPrincipalBuilder.Agent("lead").AsAdmin().WithRank(Rank.Director).Build();

    // Trainee, no admin => fails RequireMeetingWrite and RequireLeadership
    private static ClaimsPrincipal LowActor() =>
        ClaimsPrincipalBuilder.Agent("low").WithRank(Rank.Trainee).Build();

    // TeamLead without admin => IsOnlyReader => fails RequireWriteAccess
    private static ClaimsPrincipal ReaderActor() =>
        ClaimsPrincipalBuilder.Agent("reader").AsTeamLead().Build();

    // rank/supervision early access to agendas (MayAgenda set)
    private static ViewerScope MayAgendaScope() => new(true, true, "lead", null, MayAgenda: true);

    // internal agent without the agenda rank: sees an agenda only once its meeting is 2h past
    private static ViewerScope InternalNoAgendaScope() => new(false, false, "low", null, MayAgenda: false);

    // external partner: never sees an agenda, however old the meeting
    private static ViewerScope PartnerScope() => new(false, false, "p", PartnerAgency.DoJ, MayAgenda: false);

    private static Meeting NewMeeting(string id, DateTime start, string title = "Wochenrunde",
        Action<Meeting>? configure = null)
    {
        var m = new Meeting
        {
            Id = id,
            CaseNumber = "BS-" + id,
            Title = title,
            Start = start,
            Status = MeetingStatus.Planned,
        };
        configure?.Invoke(m);
        return m;
    }

    private static readonly DateTime BaseStart = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- reads

    [Fact]
    public async Task GetListAsync_OrdersByStartDescending_WithAgendaCountAndOwnSignOff()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.Meetings.Add(NewMeeting("m2", BaseStart.AddMonths(2)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "B", Sorting = 20 });
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "me", Reason = "Urlaub" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var list = await svc.GetListAsync("me");

        Assert.Equal(2, list.Count);
        Assert.Equal("m2", list[0].Id); // later start first
        Assert.Equal("m1", list[1].Id);
        Assert.Equal(2, list[1].AgendaCount);
        Assert.True(list[1].OwnSignedOff);
        Assert.False(list[0].OwnSignedOff);
    }

    [Fact]
    public async Task GetListAsync_And_GetCountAsync_ApplyDateWindow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("june", BaseStart));
            db.Meetings.Add(NewMeeting("august", BaseStart.AddMonths(2)));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var day = new DateOnly(2026, 6, 15);
        var list = await svc.GetListAsync(null, from: day, to: day);
        var count = await svc.GetCountAsync(from: day, to: day);

        Assert.Equal(1, count);
        Assert.Single(list);
        Assert.Equal("june", list[0].Id);
    }

    [Fact]
    public async Task GetCountAsync_CountsAllWithoutWindow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.Meetings.Add(NewMeeting("m2", BaseStart.AddDays(1)));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Equal(2, await svc.GetCountAsync());
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsMeeting_OrNull()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, title: "Lagebesprechung"));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var found = await svc.GetDetailAsync("m1");
        Assert.NotNull(found);
        Assert.Equal("Lagebesprechung", found!.Title);
        Assert.Null(await svc.GetDetailAsync("nope"));
    }

    [Fact]
    public async Task SearchAsync_MatchesTitleOrCaseNumber_AndReturnsAllOnEmpty()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, title: "Einsatzplanung"));
            db.Meetings.Add(NewMeeting("m2", BaseStart.AddDays(1), title: "Wochenrunde"));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var hit = await svc.SearchAsync("Einsatz");
        Assert.Single(hit);
        Assert.Equal("m1", hit[0].Id);

        var all = await svc.SearchAsync("   ");
        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlySoftDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("live", BaseStart));
            db.Meetings.Add(NewMeeting("trashed", BaseStart.AddDays(1), configure: m =>
            {
                m.IsDeleted = true;
                m.DeletedAt = BaseStart.AddDays(2);
            }));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();
        Assert.Single(trash);
        Assert.Equal("trashed", trash[0].Id);
    }

    [Fact]
    public async Task HasNextAsync_TrueWhenFollowUpExists()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("src", BaseStart));
            db.Meetings.Add(NewMeeting("next", BaseStart.AddDays(14), configure: m => m.PreviousMeetingId = "src"));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.True(await svc.HasNextAsync("src"));
        Assert.False(await svc.HasNextAsync("next"));
    }

    // ------------------------------------------------------- meeting writes

    [Fact]
    public async Task CreateAsync_PersistsMeeting_WithTrimmedFieldsAndCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);
        var input = new MeetingInput
        {
            Title = "  Führungsrunde  ",
            Start = new DateTime(2026, 6, 15, 14, 0, 0),
            End = new DateTime(2026, 6, 15, 15, 0, 0),
            Location = "  Lagezentrum  ",
            Status = MeetingStatus.Planned,
        };

        var created = await svc.CreateAsync(input, WriteActor());

        using var read = ctx.NewContext();
        var row = read.Meetings.Single();
        Assert.Equal(created.Id, row.Id);
        Assert.Equal("Führungsrunde", row.Title);
        Assert.Equal("Lagezentrum", row.Location);
        Assert.False(string.IsNullOrWhiteSpace(row.CaseNumber));
        Assert.NotNull(row.End);
    }

    [Fact]
    public async Task CreateAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);
        var input = new MeetingInput { Title = "X", Start = BaseStart };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.CreateAsync(input, LowActor()));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenStartMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);
        var input = new MeetingInput { Title = "X", Start = null };

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.CreateAsync(input, WriteActor()));
    }

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndClearsReminderStampsOnMovedStart()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m =>
            {
                m.ReminderDaySentAt = BaseStart.AddDays(-1);
                m.ReminderSoonSentAt = BaseStart.AddMinutes(-30);
            }));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);
        var input = new MeetingInput
        {
            Title = "  Neu  ",
            Start = new DateTime(2026, 7, 1, 10, 0, 0),
            Status = MeetingStatus.Postponed,
        };

        await svc.RefreshAsync("m1", input, WriteActor());

        using var read = ctx.NewContext();
        var row = read.Meetings.Single();
        Assert.Equal("Neu", row.Title);
        Assert.Equal(MeetingStatus.Postponed, row.Status);
        Assert.Null(row.ReminderDaySentAt);
        Assert.Null(row.ReminderSoonSentAt);
    }

    [Fact]
    public async Task RefreshAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);
        var input = new MeetingInput { Title = "X", Start = BaseStart };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RefreshAsync("m1", input, LowActor()));
    }

    [Fact]
    public async Task DeleteAsync_RemovesFromActiveSet()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.DeleteAsync("m1", WriteActor());

        // no soft-delete interceptor in the test context -> Remove() hard-deletes here
        using var read = ctx.NewContext();
        Assert.False(read.Meetings.Any(m => m.Id == "m1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.DeleteAsync("m1", LowActor()));
    }

    [Fact]
    public async Task RestoreAsync_UndoesSoftDelete()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m =>
            {
                m.IsDeleted = true;
                m.DeletedAt = BaseStart;
                m.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.RestoreAsync("m1", WriteActor());

        using var read = ctx.NewContext();
        var row = read.Meetings.Single(m => m.Id == "m1");
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m => m.IsDeleted = true));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.RestoreAsync("m1", LowActor()));
    }

    // ---------------------------------------------------------------- clone

    [Fact]
    public async Task NextCreateAsync_ClonesOpenItemsAndLinks_FourteenDaysLater()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("src", BaseStart, title: "Weekly", configure: m =>
            {
                m.Location = "Room 1";
                m.End = BaseStart.AddHours(1);
            }));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "open", MeetingId = "src", Title = "Offen", Sorting = 10, Done = false });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "done", MeetingId = "src", Title = "Erledigt", Sorting = 20, Done = true });
            db.Links.Add(new Link { SourceType = ItemType, SourceId = "open", TargetType = "Person", TargetId = "p1" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var next = await svc.NextCreateAsync("src", WriteActor());

        Assert.Equal("src", next.PreviousMeetingId);
        Assert.Equal("Weekly", next.Title);
        Assert.Equal(BaseStart.AddDays(14), next.Start);

        using var read = ctx.NewContext();
        var copied = Assert.Single(read.MeetingAgendaItems.Where(p => p.MeetingId == next.Id).ToList());
        Assert.Equal("Offen", copied.Title);
        Assert.Equal("open", copied.CarriedFromItemId);
        // original link + remapped copy
        Assert.Equal(2, read.Links.Count());
        Assert.True(read.Links.Any(l => l.SourceType == ItemType && l.SourceId == copied.Id && l.TargetId == "p1"));
    }

    [Fact]
    public async Task NextCreateAsync_Throws_WhenAlreadyCloned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("src", BaseStart));
            db.Meetings.Add(NewMeeting("existing", BaseStart.AddDays(14), configure: m => m.PreviousMeetingId = "src"));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.NextCreateAsync("src", WriteActor()));
    }

    [Fact]
    public async Task NextCreateAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.NextCreateAsync("src", LowActor()));
    }

    // --------------------------------------------------------------- agenda

    [Fact]
    public async Task GetAgendaAsync_FailsClosed_WhenNotAllowed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        // no meeting row and no early-access rank -> fail closed
        Assert.Empty(await svc.GetAgendaAsync("m1", InternalNoAgendaScope()));
    }

    [Fact]
    public async Task GetAgendaAsync_ReturnsItemsOrderedBySorting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "Second", Sorting = 20 });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "First", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var items = await svc.GetAgendaAsync("m1", MayAgendaScope());
        Assert.Equal(new[] { "First", "Second" }, items.Select(i => i.Title).ToArray());
    }

    [Fact]
    public async Task GetAgendaAsync_OpensToInternalAgent_TwoHoursAfterMeeting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", DateTime.UtcNow.AddHours(-3)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Single(await svc.GetAgendaAsync("m1", InternalNoAgendaScope()));
    }

    [Fact]
    public async Task GetAgendaAsync_StaysClosedToInternalAgent_WithinTwoHours()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", DateTime.UtcNow.AddHours(-1)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAgendaAsync("m1", InternalNoAgendaScope()));
    }

    [Fact]
    public async Task GetAgendaAsync_StaysClosedToPartner_EvenAfterMeeting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", DateTime.UtcNow.AddHours(-5)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAgendaAsync("m1", PartnerScope()));
    }

    [Fact]
    public async Task GetAgendaAsync_MeasuresWindowFromEnd_WhenEndSet()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            // started 5h ago but ended only 1h ago -> end+2h not yet reached
            db.Meetings.Add(NewMeeting("m1", DateTime.UtcNow.AddHours(-5), configure: m => m.End = DateTime.UtcNow.AddHours(-1)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "A", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAgendaAsync("m1", InternalNoAgendaScope()));
    }

    [Fact]
    public async Task GetItemNoteAsync_GatedByMayAgenda()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A", NotesHtml = "<p>note</p>" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        // no meeting row and no early-access rank -> null
        Assert.Null(await svc.GetItemNoteAsync("it", InternalNoAgendaScope()));
        Assert.Equal("<p>note</p>", await svc.GetItemNoteAsync("it", MayAgendaScope()));
    }

    [Fact]
    public async Task GetItemNoteAsync_OpensToInternalAgent_TwoHoursAfterMeeting()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", DateTime.UtcNow.AddHours(-3)));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A", NotesHtml = "<p>note</p>" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        Assert.Equal("<p>note</p>", await svc.GetItemNoteAsync("it", InternalNoAgendaScope()));
    }

    [Fact]
    public async Task GetAgendaLinksAsync_ReturnsEmpty_WhenScopeMayNotSeeAgenda()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);
        var scope = new ViewerScope(false, false, null, null, MayAgenda: false);

        var result = await svc.GetAgendaLinksAsync("m1", scope);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAgendaLinksAsync_ResolvesLinksPerItem_ViaLinkService()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "i1", MeetingId = "m1", Title = "A", Sorting = 10 });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "i2", MeetingId = "m1", Title = "B", Sorting = 20 });
            db.SaveChanges();
        }
        var (svc, _, _, links) = Build(ctx);
        links.GetForRecordAsync(ItemType, Arg.Any<string>(), Arg.Any<ViewerScope>(), Arg.Any<LinkKind?>(), Arg.Any<CancellationToken>())
            .Returns(new List<LinkDisplay> { new("l1", "Person", "p1", null, "Max") });
        var scope = new ViewerScope(false, false, null, null, MayAgenda: true);

        var result = await svc.GetAgendaLinksAsync("m1", scope);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey("i1"));
        Assert.True(result.ContainsKey("i2"));
        Assert.All(result.Values, v => Assert.Single(v));
    }

    [Fact]
    public async Task AgendaItemCreateAsync_AppendsWithNextSortKey()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { MeetingId = "m1", Title = "Existing", Sorting = 30 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var item = await svc.AgendaItemCreateAsync("m1", new MeetingAgendaItemInput { Title = "  Neu  " }, WriteActor());

        Assert.Equal("Neu", item.Title);
        Assert.Equal(40, item.Sorting);
        using var read = ctx.NewContext();
        Assert.Equal(2, read.MeetingAgendaItems.Count(p => p.MeetingId == "m1"));
    }

    [Fact]
    public async Task AgendaItemCreateAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgendaItemCreateAsync("m1", new MeetingAgendaItemInput { Title = "X" }, LowActor()));
    }

    [Fact]
    public async Task AgendaItemCreateAsync_Throws_OnEmptyTitle()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgendaItemCreateAsync("m1", new MeetingAgendaItemInput { Title = "   " }, WriteActor()));
    }

    [Fact]
    public async Task AgendaItemCreateAsync_Throws_WhenMeetingMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgendaItemCreateAsync("nope", new MeetingAgendaItemInput { Title = "X" }, WriteActor()));
    }

    [Fact]
    public async Task AgendaItemRefreshAsync_UpdatesTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "Old", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AgendaItemRefreshAsync("it", new MeetingAgendaItemInput { Title = "  New  " }, WriteActor());

        using var read = ctx.NewContext();
        Assert.Equal("New", read.MeetingAgendaItems.Single(p => p.Id == "it").Title);
    }

    [Fact]
    public async Task AgendaItemRefreshAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "Old" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgendaItemRefreshAsync("it", new MeetingAgendaItemInput { Title = "New" }, LowActor()));
    }

    [Fact]
    public async Task AgendaItemRemoveAsync_DeletesItem()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AgendaItemRemoveAsync("it", WriteActor());

        using var read = ctx.NewContext();
        Assert.False(read.MeetingAgendaItems.Any(p => p.Id == "it"));
    }

    [Fact]
    public async Task AgendaItemRemoveAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AgendaItemRemoveAsync("it", LowActor()));
    }

    [Fact]
    public async Task AgendaItemMoveAsync_SwapsSortKeysWithNeighbour()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "i1", MeetingId = "m1", Title = "First", Sorting = 10 });
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "i2", MeetingId = "m1", Title = "Second", Sorting = 20 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AgendaItemMoveAsync("i2", -1, WriteActor());

        using var read = ctx.NewContext();
        Assert.Equal(20, read.MeetingAgendaItems.Single(p => p.Id == "i1").Sorting);
        Assert.Equal(10, read.MeetingAgendaItems.Single(p => p.Id == "i2").Sorting);
    }

    [Fact]
    public async Task AgendaItemMoveAsync_NoOp_WhenNoNeighbour()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "i1", MeetingId = "m1", Title = "First", Sorting = 10 });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        // top item moved up: no neighbour -> silent no-op
        await svc.AgendaItemMoveAsync("i1", -1, WriteActor());

        using var read = ctx.NewContext();
        Assert.Equal(10, read.MeetingAgendaItems.Single(p => p.Id == "i1").Sorting);
    }

    [Fact]
    public async Task AgendaItemMoveAsync_Throws_ForNonMeetingWriter()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.AgendaItemMoveAsync("i1", 1, LowActor()));
    }

    [Fact]
    public async Task AgendaItemNoteAsync_SetsNoteAndDoneStamp()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AgendaItemNoteAsync("it", "<b>done note</b>", done: true, WriteActor());

        using var read = ctx.NewContext();
        var item = read.MeetingAgendaItems.Single(p => p.Id == "it");
        Assert.False(string.IsNullOrWhiteSpace(item.NotesHtml));
        Assert.True(item.Done);
        Assert.NotNull(item.DoneAt);
        Assert.Equal("lead", item.DoneById);
    }

    [Fact]
    public async Task AgendaItemNoteAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.MeetingAgendaItems.Add(new MeetingAgendaItem { Id = "it", MeetingId = "m1", Title = "A" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgendaItemNoteAsync("it", "x", true, LowActor()));
    }

    [Fact]
    public async Task MinutesAsync_StoresSanitizedMinutes()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.MinutesAsync("m1", "<p>Protokolltext</p>", WriteActor());

        using var read = ctx.NewContext();
        var row = read.Meetings.Single();
        Assert.False(string.IsNullOrWhiteSpace(row.MinutesHtml));
        Assert.Contains("Protokolltext", row.MinutesHtml!);
    }

    [Fact]
    public async Task MinutesAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.MinutesAsync("m1", "x", LowActor()));
    }

    // ----------------------------------------------------------- attendance

    [Fact]
    public async Task GetAttendanceAsync_ReturnsEmpty_WhenMeetingMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        Assert.Empty(await svc.GetAttendanceAsync("nope", mayReason: true, meId: null));
    }

    [Fact]
    public async Task GetAttendanceAsync_DerivesLiveRoster_WhileOpen()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Users.Add(Seed.Agent("a2"));
            db.Users.Add(Seed.Agent("a3"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = "m1", AgentId = "a1", Status = MeetingAttendanceStatus.Present, MarkedAt = BaseStart });
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "a2", Reason = "Urlaub" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var rows = await svc.GetAttendanceAsync("m1", mayReason: true, meId: null);

        Assert.Equal(3, rows.Count);
        var byId = rows.ToDictionary(r => r.AgentId);
        Assert.Equal(MeetingAttendanceStatus.Present, byId["a1"].Status);
        Assert.Equal(MeetingAttendanceStatus.SignedOff, byId["a2"].Status);
        Assert.Equal(MeetingAbsenceOrigin.MeetingSignOff, byId["a2"].Origin);
        Assert.Equal("Urlaub", byId["a2"].Reason);
        Assert.Equal(MeetingAttendanceStatus.Open, byId["a3"].Status);
        // Missing(none) -> Open -> Present -> SignedOff; a3 open sorts first
        Assert.Equal("a3", rows[0].AgentId);
    }

    [Fact]
    public async Task GetAttendanceAsync_HidesReason_WhenNotAllowedAndNotOwn()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a2"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "a2", Reason = "Urlaub" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var rows = await svc.GetAttendanceAsync("m1", mayReason: false, meId: "someone-else");
        Assert.Null(rows.Single().Reason);
    }

    [Fact]
    public async Task GetAttendanceAsync_ReturnsFrozenSnapshot_WhenClosed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "LiveName"));
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m => m.AttendanceClosedAt = BaseStart.AddHours(2)));
            db.MeetingAttendances.Add(new MeetingAttendance
            {
                MeetingId = "m1",
                AgentId = "a1",
                AgentCodename = "FrozenName",
                Status = MeetingAttendanceStatus.Present,
                Origin = MeetingAbsenceOrigin.None,
                MarkedAt = BaseStart,
            });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        var row = Assert.Single(await svc.GetAttendanceAsync("m1", mayReason: true, meId: null));
        Assert.Equal("FrozenName", row.Codename); // snapshot codename preferred over the live one
        Assert.Equal(MeetingAttendanceStatus.Present, row.Status);
    }

    [Fact]
    public async Task AttendanceSetAsync_CreatesPresentRow()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AttendanceSetAsync("m1", "a1", MeetingAttendanceStatus.Present, WriteActor());

        using var read = ctx.NewContext();
        var row = read.MeetingAttendances.Single(t => t.MeetingId == "m1" && t.AgentId == "a1");
        Assert.Equal(MeetingAttendanceStatus.Present, row.Status);
        Assert.Equal(MeetingAbsenceOrigin.None, row.Origin);
        Assert.Equal("lead", row.MarkedById);
    }

    [Fact]
    public async Task AttendanceSetAsync_RemovesRow_WhenSetToOpen()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = "m1", AgentId = "a1", Status = MeetingAttendanceStatus.Missing, MarkedAt = BaseStart });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.AttendanceSetAsync("m1", "a1", MeetingAttendanceStatus.Open, WriteActor());

        using var read = ctx.NewContext();
        Assert.False(read.MeetingAttendances.Any(t => t.MeetingId == "m1" && t.AgentId == "a1"));
    }

    [Fact]
    public async Task AttendanceSetAsync_Throws_WhenAlreadyClosed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m => m.AttendanceClosedAt = BaseStart.AddHours(2)));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AttendanceSetAsync("m1", "a1", MeetingAttendanceStatus.Present, WriteActor()));
    }

    [Fact]
    public async Task AttendanceSetAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AttendanceSetAsync("m1", "a1", MeetingAttendanceStatus.Present, LowActor()));
    }

    [Fact]
    public async Task CloseAttendanceAsync_FreezesRoster_AndMarksHeld()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Users.Add(Seed.Agent("a2"));
            db.Users.Add(Seed.Agent("a3"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = "m1", AgentId = "a1", Status = MeetingAttendanceStatus.Present, MarkedAt = BaseStart });
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "a2", Reason = "Krank" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.CloseAttendanceAsync("m1", confirmAllMissing: false, WriteActor());

        using var read = ctx.NewContext();
        var meeting = read.Meetings.Single(m => m.Id == "m1");
        Assert.NotNull(meeting.AttendanceClosedAt);
        Assert.Equal(MeetingStatus.Held, meeting.Status);

        var byId = read.MeetingAttendances.Where(t => t.MeetingId == "m1").ToDictionary(t => t.AgentId);
        Assert.Equal(3, byId.Count);
        Assert.Equal(MeetingAttendanceStatus.Present, byId["a1"].Status);
        Assert.Equal("Codename-a1", byId["a1"].AgentCodename); // codename snapshotted at close
        Assert.Equal(MeetingAttendanceStatus.SignedOff, byId["a2"].Status);
        Assert.Equal(MeetingAbsenceOrigin.MeetingSignOff, byId["a2"].Origin);
        Assert.Equal("Krank", byId["a2"].Reason);
        Assert.Equal(MeetingAttendanceStatus.Missing, byId["a3"].Status);
    }

    [Fact]
    public async Task CloseAttendanceAsync_UsesAbsenceCoveringTheDay()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Users.Add(Seed.Agent("a2"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = "m1", AgentId = "a1", Status = MeetingAttendanceStatus.Present, MarkedAt = BaseStart });
            db.Absences.Add(new Absence
            {
                AgentId = "a2",
                FromDate = new DateOnly(2026, 6, 14),
                ToDate = new DateOnly(2026, 6, 16),
                Category = AbsenceCategory.Vacation,
                Reason = "Urlaub",
                CreatedAt = BaseStart.AddDays(-1), // filed before the meeting began
            });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.CloseAttendanceAsync("m1", confirmAllMissing: false, WriteActor());

        using var read = ctx.NewContext();
        var a2 = read.MeetingAttendances.Single(t => t.MeetingId == "m1" && t.AgentId == "a2");
        Assert.Equal(MeetingAttendanceStatus.SignedOff, a2.Status);
        Assert.Equal(MeetingAbsenceOrigin.Absence, a2.Origin);
        Assert.Equal("Urlaub", a2.Reason);
    }

    [Fact]
    public async Task CloseAttendanceAsync_Throws_OnAllMissingWithoutConfirmation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Users.Add(Seed.Agent("a2"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CloseAttendanceAsync("m1", confirmAllMissing: false, WriteActor()));
    }

    [Fact]
    public async Task CloseAttendanceAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CloseAttendanceAsync("m1", true, LowActor()));
    }

    [Fact]
    public async Task ReopenAttendanceAsync_DropsSnapshot_AndRevertsHeldToPlanned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m =>
            {
                m.Status = MeetingStatus.Held;
                m.AttendanceClosedAt = BaseStart.AddHours(2);
            }));
            db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = "m1", AgentId = "a1", Status = MeetingAttendanceStatus.Present, MarkedAt = BaseStart });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.ReopenAttendanceAsync("m1", WriteActor());

        using var read = ctx.NewContext();
        var meeting = read.Meetings.Single(m => m.Id == "m1");
        Assert.Null(meeting.AttendanceClosedAt);
        Assert.Equal(MeetingStatus.Planned, meeting.Status);
        Assert.False(read.MeetingAttendances.Any(t => t.MeetingId == "m1"));
    }

    [Fact]
    public async Task ReopenAttendanceAsync_Throws_WhenNotClosed()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ReopenAttendanceAsync("m1", WriteActor()));
    }

    [Fact]
    public async Task ReopenAttendanceAsync_Throws_ForNonLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(NewMeeting("m1", BaseStart, configure: m => m.AttendanceClosedAt = BaseStart.AddHours(2)));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.ReopenAttendanceAsync("m1", LowActor()));
    }

    // -------------------------------------------------------- per-meeting sign-off

    [Fact]
    public async Task SignOffAsync_RecordsSignOff_WithTrimmedReason()
    {
        using var ctx = new SqliteTestContext();
        var futureStart = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", futureStart));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.SignOffAsync("m1", "  krank  ", ClaimsPrincipalBuilder.Agent("a1").Build());

        using var read = ctx.NewContext();
        var row = read.MeetingSignOffs.Single(s => s.MeetingId == "m1" && s.AgentId == "a1");
        Assert.Equal("krank", row.Reason);
    }

    [Fact]
    public async Task SignOffAsync_IsIdempotent()
    {
        using var ctx = new SqliteTestContext();
        var futureStart = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", futureStart));
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "a1", Reason = "first" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.SignOffAsync("m1", "second", ClaimsPrincipalBuilder.Agent("a1").Build());

        using var read = ctx.NewContext();
        var row = read.MeetingSignOffs.Single(s => s.MeetingId == "m1" && s.AgentId == "a1");
        Assert.Equal("first", row.Reason); // existing sign-off untouched
    }

    [Fact]
    public async Task SignOffAsync_Throws_AfterMeetingStarted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart)); // BaseStart is in the past
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SignOffAsync("m1", null, ClaimsPrincipalBuilder.Agent("a1").Build()));
    }

    [Fact]
    public async Task SignOffAsync_Throws_WhenClosed()
    {
        using var ctx = new SqliteTestContext();
        var futureStart = new DateTime(2027, 1, 10, 12, 0, 0, DateTimeKind.Utc);
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", futureStart, configure: m => m.AttendanceClosedAt = DateTime.UtcNow));
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.SignOffAsync("m1", null, ClaimsPrincipalBuilder.Agent("a1").Build()));
    }

    [Fact]
    public async Task SignOffAsync_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SignOffAsync("m1", null, ReaderActor()));
    }

    [Fact]
    public async Task SignOffRevokeAsync_RemovesOwnSignOff()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Meetings.Add(NewMeeting("m1", BaseStart));
            db.MeetingSignOffs.Add(new MeetingSignOff { MeetingId = "m1", AgentId = "a1", Reason = "x" });
            db.SaveChanges();
        }
        var (svc, _, _, _) = Build(ctx);

        await svc.SignOffRevokeAsync("m1", ClaimsPrincipalBuilder.Agent("a1").Build());

        using var read = ctx.NewContext();
        Assert.False(read.MeetingSignOffs.Any(s => s.MeetingId == "m1" && s.AgentId == "a1"));
    }

    [Fact]
    public async Task SignOffRevokeAsync_NoOp_WhenNoSignOff()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        // no matching row -> silent no-op, must not throw
        await svc.SignOffRevokeAsync("m1", ClaimsPrincipalBuilder.Agent("a1").Build());

        using var read = ctx.NewContext();
        Assert.Equal(0, read.MeetingSignOffs.Count());
    }

    [Fact]
    public async Task SignOffRevokeAsync_Throws_ForReadOnlySupervisor()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _, _, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.SignOffRevokeAsync("m1", ReaderActor()));
    }
}
