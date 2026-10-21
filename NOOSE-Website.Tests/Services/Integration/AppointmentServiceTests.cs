using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Appointments;
using NOOSE_Website.Models.Appointments;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AppointmentService"/> against in-memory SQLite.</summary>
public sealed class AppointmentServiceTests
{
    // Rank >= SupervisorySpecialAgent(4) => IsLeadership().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: neither leadership nor admin.
    private static ClaimsPrincipal NonLeader(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static (AppointmentService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("NOOSE-TM-2026-0001");
        var notifications = Substitute.For<INotificationService>();
        var svc = new AppointmentService(ctx.Factory, caseNo, notifications);
        return (svc, notifications);
    }

    private static AppointmentInput Input(string title = "Besprechung", DateTime? start = null,
        DateTime? end = null, AppointmentVisibilityLevel visibility = AppointmentVisibilityLevel.Public,
        bool allDay = false)
        => new()
        {
            Title = title,
            Category = AppointmentCategory.Meeting,
            Status = AppointmentStatus.Planned,
            Location = "HQ",
            Start = start ?? new DateTime(2026, 8, 1, 10, 0, 0),
            End = end,
            AllDay = allDay,
            Description = "Details",
            Visibility = visibility,
        };

    private static Appointment MakeAppointment(string id, string? createdById = null,
        AppointmentVisibilityLevel visibility = AppointmentVisibilityLevel.Public,
        Action<Appointment>? configure = null)
    {
        var a = new Appointment
        {
            Id = id,
            CaseNumber = $"NOOSE-TM-2026-{id}",
            Title = $"Termin {id}",
            Start = new DateTime(2026, 8, 1, 10, 0, 0, DateTimeKind.Utc),
            Visibility = visibility,
            CreatedById = createdById,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        configure?.Invoke(a);
        return a;
    }

    // ---- GetDetailAsync ----

    [Fact]
    public async Task GetDetailAsync_ReturnsPublicAppointment()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetDetailAsync("a1", NonLeader("viewer"));

        Assert.NotNull(result);
        Assert.Equal("a1", result!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_ForPrivateOfOther_WhenNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "someone-else",
                visibility: AppointmentVisibilityLevel.Private));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetDetailAsync("a1", NonLeader("intruder"));

        Assert.Null(result);
    }

    // ---- GetTrashAsync ----

    [Fact]
    public async Task GetTrashAsync_ReturnsSoftDeleted_OrderedByDeletedAtDesc()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("older", configure: a =>
            {
                a.IsDeleted = true;
                a.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Appointments.Add(MakeAppointment("newer", configure: a =>
            {
                a.IsDeleted = true;
                a.DeletedAt = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Appointments.Add(MakeAppointment("alive")); // not deleted -> excluded
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();

        Assert.Equal(new[] { "newer", "older" }, trash.Select(t => t.Id).ToArray());
    }

    // ---- SearchAsync ----

    [Fact]
    public async Task SearchAsync_FiltersByTitle_AndOrdersByStartDesc()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("m1", configure: a =>
            {
                a.Title = "Meeting Alpha";
                a.Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Appointments.Add(MakeAppointment("m2", configure: a =>
            {
                a.Title = "Meeting Beta";
                a.Start = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Appointments.Add(MakeAppointment("x1", configure: a => a.Title = "Einsatz"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.SearchAsync("Meeting", mayAll: true, meId: "any");

        // both "Meeting" rows, newest Start first; "Einsatz" excluded.
        Assert.Equal(new[] { "m2", "m1" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task SearchAsync_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.Appointments.Add(MakeAppointment($"a{i}", configure: a => a.Title = "Same"));
            }
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.SearchAsync(null, mayAll: true, meId: "any", max: 2);

        Assert.Equal(2, result.Count);
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_SkipsTeamLeadAndPartnerAgentIds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator-1"));
            db.Users.Add(Seed.Agent("ok"));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var created = await svc.CreateAsync(Input(), new[] { "ok", "tl", "partner" }, NonLeader("creator-1"));

        using var db2 = ctx.NewContext();
        var assigned = await db2.AppointmentAssignments
            .Where(z => z.AppointmentId == created.Id).Select(z => z.AgentId).ToListAsync();
        Assert.Equal(new[] { "ok" }, assigned);
    }

    [Fact]
    public async Task CreateAsync_PersistsAppointment_AssignsActiveAgents_NotifiesExceptCreator()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator-1"));
            db.Users.Add(Seed.Agent("a1"));
            db.Users.Add(Seed.Agent("a2"));
            db.Users.Add(Seed.Agent("a3", status: AgentStatus.Pending)); // inactive -> excluded
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);
        var actor = NonLeader("creator-1");

        var created = await svc.CreateAsync(
            Input(title: "  Team  "),
            new[] { "a1", "a2", "a3", "ghost", "creator-1" },
            actor);

        Assert.Equal("NOOSE-TM-2026-0001", created.CaseNumber);
        Assert.Equal("Team", created.Title);

        using var db2 = ctx.NewContext();
        var stored = await db2.Appointments.SingleAsync(a => a.Id == created.Id);
        Assert.Equal(AppointmentCategory.Meeting, stored.Category);
        var assignedIds = await db2.AppointmentAssignments
            .Where(z => z.AppointmentId == created.Id)
            .Select(z => z.AgentId)
            .ToListAsync();
        // a1, a2, creator-1 valid+active; a3 inactive and ghost unknown excluded.
        Assert.Equal(new HashSet<string> { "a1", "a2", "creator-1" }, assignedIds.ToHashSet());

        // one event for all participants; the creator is the trigger, inactive/unknown never notified
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r =>
                r.Contains("a1") && r.Contains("a2") && !r.Contains("a3") && !r.Contains("ghost")),
            NotificationType.AppointmentAssigned,
            Arg.Any<string>(), $"/kalender/{created.Id}", "creator-1", Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string?>(), NotificationType.AppointmentAssigned,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_DiscordPingGoesToParticipantsOnly_NotToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.Users.Add(Seed.Agent("guest", configure: a => a.Rank = Rank.JuniorAgent));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.CreateAsync(Input(), new[] { "guest" }, Leader("boss"));

        // the channel-posting path carries only the invited
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 1 && r.Contains("guest")),
            NotificationType.AppointmentAssigned,
            Arg.Any<string>(), Arg.Any<string>(), "boss", Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyManyAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.AppointmentScheduled,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        // leadership hears of it in the bell only
        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.AppointmentScheduled,
            Arg.Any<string>(), Arg.Any<string>(), "boss", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_AnnouncesToLeadership_ExceptCreator()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.Users.Add(Seed.Agent("admin", configure: a => a.IsAdmin = true));
            db.Users.Add(Seed.Agent("junior", configure: a => a.Rank = Rank.JuniorAgent));
            db.Users.Add(Seed.Agent("tl", configure: a =>
            {
                a.Rank = Rank.Director;
                a.IsTeamLead = true;
            }));
            db.Users.Add(Seed.Agent("partner", configure: a =>
            {
                a.Rank = Rank.Director;
                a.PartnerAgency = PartnerAgency.LSPD;
            }));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.CreateAsync(Input(title: "Gerichtstermin"), Array.Empty<string>(), Leader("boss"));

        // leadership and admin only; team lead and partner are never selectable, the creator is the trigger
        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r =>
                r.Contains("boss") && r.Contains("admin")
                && !r.Contains("junior") && !r.Contains("tl") && !r.Contains("partner")),
            NotificationType.AppointmentScheduled,
            Arg.Any<string>(), Arg.Any<string>(), "boss", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_LeadershipTitle_CarriesTitleTimeAndCategory()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        var created = await svc.CreateAsync(
            Input(title: "Razzia", start: new DateTime(2026, 8, 1, 14, 30, 0)),
            Array.Empty<string>(), NonLeader("creator-1"));

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.AppointmentScheduled,
            Arg.Is<string>(t => t.Contains("Razzia") && t.Contains("01.08.2026 14:30") && t.Contains("Interner Termin")),
            $"/kalender/{created.Id}", "creator-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_LeadershipTitle_OmitsClockTime_ForAllDay()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.CreateAsync(
            Input(title: "Feiertag", start: new DateTime(2026, 8, 1, 14, 30, 0), allDay: true),
            Array.Empty<string>(), NonLeader("creator-1"));

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            NotificationType.AppointmentScheduled,
            Arg.Is<string>(t => t.Contains("01.08.2026") && !t.Contains("Uhr")),
            Arg.Any<string>(), "creator-1", Arg.Any<CancellationToken>());
    }

    // leadership sees every appointment anyway, so no visibility level is withheld from them
    [Theory]
    [InlineData(AppointmentVisibilityLevel.Public)]
    [InlineData(AppointmentVisibilityLevel.Restricted)]
    [InlineData(AppointmentVisibilityLevel.Private)]
    public async Task CreateAsync_AnnouncesToLeadership_ForEveryVisibility(AppointmentVisibilityLevel visibility)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.CreateAsync(Input(visibility: visibility), Array.Empty<string>(), NonLeader("creator-1"));

        await notifications.Received(1).NotifyManyInAppAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Contains("boss")), NotificationType.AppointmentScheduled,
            Arg.Any<string>(), Arg.Any<string>(), "creator-1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_DoesNotAnnounceToLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("boss", configure: a => a.Rank = Rank.Director));
            db.Appointments.Add(MakeAppointment("a1", createdById: "creator-1"));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.RefreshAsync("a1", Input(title: "Verschoben"), Leader("boss"));

        await notifications.DidNotReceive().NotifyManyInAppAsync(
            Arg.Any<IReadOnlyCollection<string>>(), NotificationType.AppointmentScheduled,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_NoAssignments_WhenAgentIdsEmpty()
    {
        using var ctx = new SqliteTestContext();
        var (svc, notifications) = Build(ctx);

        var created = await svc.CreateAsync(Input(), Array.Empty<string>(), NonLeader("creator-1"));

        using var db = ctx.NewContext();
        Assert.False(await db.AppointmentAssignments.AnyAsync(z => z.AppointmentId == created.Id));
        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenStartMissing()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = Input();
        input.Start = null;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Array.Empty<string>(), NonLeader("creator-1")));
    }

    [Fact]
    public async Task CreateAsync_Throws_WhenEndBeforeStart()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = Input(
            start: new DateTime(2026, 8, 1, 12, 0, 0),
            end: new DateTime(2026, 8, 1, 10, 0, 0));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Array.Empty<string>(), NonLeader("creator-1")));
    }

    // ---- RefreshAsync ----

    [Fact]
    public async Task RefreshAsync_UpdatesMasterData_AsLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.RefreshAsync("a1", Input(title: "Geändert", visibility: AppointmentVisibilityLevel.Restricted), Leader());

        using var check = ctx.NewContext();
        var stored = await check.Appointments.SingleAsync(a => a.Id == "a1");
        Assert.Equal("Geändert", stored.Title);
        Assert.Equal(AppointmentVisibilityLevel.Restricted, stored.Visibility);
    }

    [Fact]
    public async Task RefreshAsync_AllowsCreator_NonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "creator-1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.RefreshAsync("a1", Input(title: "Vom Ersteller"), NonLeader("creator-1"));

        using var check = ctx.NewContext();
        var stored = await check.Appointments.SingleAsync(a => a.Id == "a1");
        Assert.Equal("Vom Ersteller", stored.Title);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotCreatorNorLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "creator-1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("a1", Input(), NonLeader("intruder")));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", Input(), Leader()));
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task DeleteAsync_RemovesAppointment_AsLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.DeleteAsync("a1", Leader());

        // interceptor absent in tests -> hard delete, gone from the filtered set.
        using var check = ctx.NewContext();
        Assert.False(await check.Appointments.AnyAsync(a => a.Id == "a1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotCreatorNorLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", createdById: "creator-1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("a1", NonLeader("intruder")));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("missing", Leader()));
    }

    // ---- RestoreAsync ----

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("any", NonLeader("junior")));
    }

    [Fact]
    public async Task RestoreAsync_RestoresSoftDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("a1", configure: a =>
            {
                a.IsDeleted = true;
                a.DeletedAt = new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc);
                a.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.RestoreAsync("a1", Leader());

        using var check = ctx.NewContext();
        // reappears in the filtered set with delete markers cleared.
        var stored = await check.Appointments.SingleAsync(a => a.Id == "a1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    // ---- GetParticipantAsync ----

    [Fact]
    public async Task GetParticipantAsync_ReturnsAssignments_OrderedByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Bravo"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Alpha"));
            db.Appointments.Add(MakeAppointment("t1"));
            db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "t1", AgentId = "a1" });
            db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "t1", AgentId = "a2" });
            // assignment on another appointment must be excluded.
            db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "other", AgentId = "a1" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var participants = await svc.GetParticipantAsync("t1");

        Assert.Equal(new[] { "Alpha", "Bravo" }, participants.Select(p => p.Agent!.Codename).ToArray());
    }

    // ---- AgentAssignAsync ----

    [Fact]
    public async Task AgentAssignAsync_AssignsAndNotifies_AsLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.AgentAssignAsync("t1", "a1", Leader());

        using var check = ctx.NewContext();
        Assert.True(await check.AppointmentAssignments.AnyAsync(z => z.AppointmentId == "t1" && z.AgentId == "a1"));
        // the channel-posting path, so the new participant alone is pinged
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 1 && r.Contains("a1")),
            NotificationType.AppointmentAssigned,
            Arg.Any<string>(), "/kalender/t1", Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentAssignAsync_DoesNotNotify_WhenAssigningSelf()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("lead"));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.AgentAssignAsync("t1", "lead", Leader("lead"));

        await notifications.DidNotReceive().NotifyAsync(Arg.Any<string>(), Arg.Any<NotificationType>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await notifications.DidNotReceive().NotifyManyAsync(Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<NotificationType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenNotCreatorNorLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Appointments.Add(MakeAppointment("t1", createdById: "creator-1"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAssignAsync("t1", "a1", NonLeader("intruder")));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentInactive()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", status: AgentStatus.Pending));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("t1", "a1", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentIsTeamLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.IsTeamLead = true));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("t1", "a1", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentIsPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("t1", "a1", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAlreadyAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.AppointmentAssignments.Add(new AppointmentAssignment { AppointmentId = "t1", AgentId = "a1" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("t1", "a1", Leader()));
    }

    // ---- AgentRemoveAsync ----

    [Fact]
    public async Task AgentRemoveAsync_Removes_AsLeader()
    {
        using var ctx = new SqliteTestContext();
        var assignment = new AppointmentAssignment { Id = "z1", AppointmentId = "t1", AgentId = "a1" };
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("t1", createdById: "someone-else"));
            db.AppointmentAssignments.Add(assignment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.AgentRemoveAsync("z1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.AppointmentAssignments.AnyAsync(z => z.Id == "z1"));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenUnknown()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // returns without throwing when the assignment does not exist.
        await svc.AgentRemoveAsync("missing", NonLeader("anyone"));

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.AppointmentAssignments.CountAsync());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenNotCreatorNorLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Appointments.Add(MakeAppointment("t1", createdById: "creator-1"));
            db.AppointmentAssignments.Add(new AppointmentAssignment { Id = "z1", AppointmentId = "t1", AgentId = "a1" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync("z1", NonLeader("intruder")));
    }
}
