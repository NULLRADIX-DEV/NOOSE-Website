using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Jobs;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Jobs;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="JobService"/> over in-memory SQLite.</summary>
public sealed class JobServiceTests
{
    // Rank >= SpecialAgentInCharge(4) => IsLeadership(); MayWrite/MayClassifiedRead true.
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, MayWrite true, MayClassifiedRead false.
    private static ClaimsPrincipal Member(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    private static (JobService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<NOOSE_Website.Data.AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("FIB-A-2026-0001");
        var notifications = Substitute.For<INotificationService>();
        var svc = new JobService(ctx.Factory, caseNo, notifications);
        return (svc, notifications);
    }

    private static Job NewJob(string id, string title, string? createdById,
        Action<Job>? configure = null)
    {
        var job = new Job
        {
            Id = id,
            Title = title,
            CaseNumber = $"FIB-A-2026-{id}",
            CreatedById = createdById,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Status = JobStatus.Open,
            Priority = JobPriority.Normal,
        };
        configure?.Invoke(job);
        return job;
    }

    // ---------- GetTeamBoardAsync ----------

    [Fact]
    public async Task GetTeamBoardAsync_ReturnsRows_NewestFirst_WithCodenamesAndAssignments()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("creator", configure: a => a.Codename = "Falcon"));
            db.Users.Add(Seed.Agent("assignee", configure: a => a.Codename = "Wolf"));
            db.Jobs.Add(NewJob("j1", "Alpha", "creator",
                j => j.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Jobs.Add(NewJob("j2", "Beta", "creator",
                j => j.CreatedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "assignee" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var rows = await svc.GetTeamBoardAsync(false, Leader());

        // newest CreatedAt first (j2 before j1)
        Assert.Equal(new[] { "j2", "j1" }, rows.Select(r => r.Id).ToArray());
        var j1 = rows.Single(r => r.Id == "j1");
        Assert.Equal("Falcon", j1.CreatorCodename);
        Assert.Equal(new[] { "Wolf" }, j1.AssignedCodenames.ToArray());
        // leader may always change status
        Assert.True(j1.MayStatusChange);
    }

    [Fact]
    public async Task GetTeamBoardAsync_OnlyMy_FiltersToCreatorOrAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("me", configure: a => a.Codename = "Me"));
            db.Jobs.Add(NewJob("mine", "Mine", "me"));
            db.Jobs.Add(NewJob("assignedToMe", "Assigned", "other"));
            db.Jobs.Add(NewJob("theirs", "Theirs", "other"));
            db.JobAssignments.Add(new JobAssignment { JobId = "assignedToMe", AgentId = "me" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        // leader sees all jobs, but onlyMy narrows to creator/assigned
        var rows = await svc.GetTeamBoardAsync(true, Leader("me"));

        Assert.Equal(new HashSet<string> { "mine", "assignedToMe" }, rows.Select(r => r.Id).ToHashSet());
    }

    [Fact]
    public async Task GetTeamBoardAsync_RestrictedJob_HiddenFromUninvolvedNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("open", "Open", "other"));
            db.Jobs.Add(NewJob("restricted", "Secret", "other", j => j.IsRestricted = true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var rows = await svc.GetTeamBoardAsync(false, Member("stranger"));

        Assert.Equal(new[] { "open" }, rows.Select(r => r.Id).ToArray());
    }

    [Fact]
    public async Task GetTeamBoardAsync_MayStatusChange_FalseForUninvolvedNonLeader()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("open", "Open", "other"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var rows = await svc.GetTeamBoardAsync(false, Member("stranger"));

        Assert.False(rows.Single().MayStatusChange);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_ReturnsJob_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Detail", "other"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var job = await svc.GetDetailAsync("j1", Leader());

        Assert.NotNull(job);
        Assert.Equal("Detail", job!.Title);
    }

    [Fact]
    public async Task GetDetailAsync_ReturnsNull_WhenRestrictedAndNotInvolved()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Secret", "other", j => j.IsRestricted = true));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var job = await svc.GetDetailAsync("j1", Member("stranger"));

        Assert.Null(job);
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlySoftDeleted()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("live", "Live", "other"));
            db.Jobs.Add(NewJob("gone", "Gone", "other", j =>
            {
                j.IsDeleted = true;
                j.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var trash = await svc.GetTrashAsync();

        Assert.Equal(new[] { "gone" }, trash.Select(j => j.Id).ToArray());
    }

    // ---------- SearchAsync ----------

    [Fact]
    public async Task SearchAsync_FiltersByText_OrderedByTitle()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Zulu report", "other"));
            db.Jobs.Add(NewJob("j2", "Alpha report", "other"));
            db.Jobs.Add(NewJob("j3", "Unrelated", "other"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.SearchAsync("report", mayAll: true, meId: "x");

        Assert.Equal(new[] { "Alpha report", "Zulu report" }, result.Select(j => j.Title).ToArray());
    }

    [Fact]
    public async Task SearchAsync_RespectsMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            for (var i = 0; i < 5; i++)
            {
                db.Jobs.Add(NewJob($"j{i}", $"Match {i}", "other"));
            }
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.SearchAsync("Match", mayAll: true, meId: "x", max: 2);

        Assert.Equal(2, result.Count);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_SkipsTeamLeadAndPartnerAgentIds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("ok"));
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        var job = await svc.CreateAsync(new JobInput { Title = "T", Status = JobStatus.Open },
            new[] { "ok", "tl", "partner" }, Leader("creator"));

        using (var db = ctx.NewContext())
        {
            var assignments = await db.JobAssignments.Where(z => z.JobId == job.Id)
                .Select(z => z.AgentId).ToListAsync();
            Assert.Equal(new[] { "ok" }, assignments.ToArray());
        }
        // only the surviving assignee is notified
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 1 && r.Contains("ok")),
            NotificationType.JobAssigned, Arg.Any<string>(), Arg.Any<string>(), "creator",
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_PersistsJob_AssignsOnlyActive_AndNotifies()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("active", status: AgentStatus.Active));
            db.Users.Add(Seed.Agent("inactive", status: AgentStatus.Pending));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);
        var input = new JobInput { Title = "  New Task  ", Description = "  do it  ", Status = JobStatus.Open };

        var job = await svc.CreateAsync(input, new[] { "active", "inactive" }, Leader("creator"));

        Assert.Equal("FIB-A-2026-0001", job.CaseNumber);
        Assert.Equal("New Task", job.Title);
        Assert.Equal("do it", job.Description);
        using (var db = ctx.NewContext())
        {
            var stored = await db.Jobs.SingleAsync(j => j.Id == job.Id);
            Assert.Equal("New Task", stored.Title);
            var assignments = await db.JobAssignments.Where(z => z.JobId == job.Id).Select(z => z.AgentId).ToListAsync();
            // only the active agent is assigned
            Assert.Equal(new[] { "active" }, assignments.ToArray());
        }
        // notified the active assignee, excluding the creator via triggerId
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Count == 1 && r.Contains("active")),
            NotificationType.JobAssigned, Arg.Any<string>(), Arg.Any<string>(), "creator", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateAsync_SetsDoneAt_WhenStatusCompleted()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var input = new JobInput { Title = "Done already", Status = JobStatus.Done };

        var job = await svc.CreateAsync(input, Array.Empty<string>(), Leader("creator"));

        Assert.NotNull(job.DoneAt);
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_UpdatesFields_AndResetsReminder_OnDueChange()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Old", "creator", j =>
            {
                j.DueDate = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc);
                j.DueReminderStage = JobDueReminderStage.DueDay;
            }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);
        var input = new JobInput
        {
            Title = "  New  ",
            Description = "  desc  ",
            Priority = JobPriority.High,
            Status = JobStatus.InProcessing,
            DueDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };

        await svc.RefreshAsync("j1", input, Member("creator"));

        using var check = ctx.NewContext();
        var stored = await check.Jobs.SingleAsync(j => j.Id == "j1");
        Assert.Equal("New", stored.Title);
        Assert.Equal("desc", stored.Description);
        Assert.Equal(JobPriority.High, stored.Priority);
        Assert.Equal(JobStatus.InProcessing, stored.Status);
        // rescheduled due date rearms the reminder ladder
        Assert.Equal(JobDueReminderStage.None, stored.DueReminderStage);
        // not completed => no done timestamp
        Assert.Null(stored.DoneAt);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Old", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("j1", new JobInput { Title = "x" }, Member("stranger")));
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", new JobInput { Title = "x" }, Leader()));
    }

    // ---------- StatusSetAsync ----------

    [Fact]
    public async Task StatusSetAsync_AssigneeSetsDone_SetsDoneAt_AndNotifiesCreator()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "assignee" });
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.StatusSetAsync("j1", JobStatus.Done, Member("assignee"));

        using (var check = ctx.NewContext())
        {
            var stored = await check.Jobs.SingleAsync(j => j.Id == "j1");
            Assert.Equal(JobStatus.Done, stored.Status);
            Assert.NotNull(stored.DoneAt);
        }
        // creator (who did not complete it) is notified
        await notifications.Received(1).NotifyAsync("creator", NotificationType.JobAssigned,
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StatusSetAsync_Throws_WhenNotInvolvedOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.StatusSetAsync("j1", JobStatus.Done, Member("stranger")));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_LeaderDeletes_RowGone()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Doomed", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.DeleteAsync("j1", Leader());

        // no soft-delete interceptor in tests => hard delete, gone from the set
        using var check = ctx.NewContext();
        Assert.False(await check.Jobs.IgnoreQueryFilters().AnyAsync(j => j.Id == "j1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("j1", Member("stranger")));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_LeaderRestores_ClearsDeletedFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Gone", "creator", j =>
            {
                j.IsDeleted = true;
                j.DeletedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
                j.DeletedById = "creator";
            }));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.RestoreAsync("j1", Leader());

        using var check = ctx.NewContext();
        var stored = await check.Jobs.SingleAsync(j => j.Id == "j1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // guard is the first statement, before any DB access
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("any", Member("junior")));
    }

    // ---------- GetAssignmentsAsync ----------

    [Fact]
    public async Task GetAssignmentsAsync_ReturnsAssignments_OrderedByCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1", configure: a => a.Codename = "Zebra"));
            db.Users.Add(Seed.Agent("a2", configure: a => a.Codename = "Apex"));
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "a1" });
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "a2" });
            db.JobAssignments.Add(new JobAssignment { JobId = "other", AgentId = "a1" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var result = await svc.GetAssignmentsAsync("j1");

        Assert.Equal(new[] { "Apex", "Zebra" }, result.Select(z => z.Agent!.Codename).ToArray());
    }

    // ---------- AgentAssignAsync ----------

    [Fact]
    public async Task AgentAssignAsync_LeaderAssignsActiveAgent_Persists_AndNotifies()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("active", status: AgentStatus.Active));
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, notifications) = Build(ctx);

        await svc.AgentAssignAsync("j1", "active", Leader("lead"));

        using (var check = ctx.NewContext())
        {
            Assert.True(await check.JobAssignments.AnyAsync(z => z.JobId == "j1" && z.AgentId == "active"));
        }
        await notifications.Received(1).NotifyManyAsync(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Contains("active")),
            NotificationType.JobAssigned, Arg.Any<string>(), Arg.Any<string>(), "lead", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentNotActiveOrMissing()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("j1", "ghost", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentIsTeamLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("j1", "tl", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAgentIsPartner()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("j1", "partner", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenAlreadyAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("active", status: AgentStatus.Active));
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(new JobAssignment { JobId = "j1", AgentId = "active" });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAssignAsync("j1", "active", Leader()));
    }

    [Fact]
    public async Task AgentAssignAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAssignAsync("j1", "active", Member("stranger")));
    }

    // ---------- AgentRemoveAsync ----------

    [Fact]
    public async Task AgentRemoveAsync_LeaderRemoves_AssignmentGone()
    {
        using var ctx = new SqliteTestContext();
        var assignment = new JobAssignment { JobId = "j1", AgentId = "active" };
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(assignment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await svc.AgentRemoveAsync(assignment.Id, Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.JobAssignments.AnyAsync(z => z.Id == assignment.Id));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenAssignmentUnknown()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // returns silently for an unknown assignment (no throw)
        await svc.AgentRemoveAsync("missing", Member("stranger"));

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.JobAssignments.CountAsync());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenNotCreatorOrLeadership()
    {
        using var ctx = new SqliteTestContext();
        var assignment = new JobAssignment { JobId = "j1", AgentId = "active" };
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(assignment);
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync(assignment.Id, Member("stranger")));
    }

    // ---------- GetHistoryAsync ----------

    [Fact]
    public async Task GetHistoryAsync_ReturnsJobAndAssignmentLogs_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var assignment = new JobAssignment { JobId = "j1", AgentId = "a1" };
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Task", "creator"));
            db.JobAssignments.Add(assignment);
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Job), EntityId = "j1", Action = AuditAction.Created,
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(JobAssignment), EntityId = assignment.Id, Action = AuditAction.Created,
                Timestamp = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            // unrelated log must be excluded
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person", EntityId = "p1", Action = AuditAction.Created,
                Timestamp = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var history = await svc.GetHistoryAsync("j1");

        Assert.Equal(2, history.Count);
        // newest first: the assignment log (Feb) precedes the job log (Jan)
        Assert.Equal(nameof(JobAssignment), history[0].EntityType);
        Assert.Equal(nameof(Job), history[1].EntityType);
    }

    // ---------- ReferenceDisplayAsync ----------

    [Fact]
    public async Task ReferenceDisplayAsync_ReturnsDisplay_ForVisibleJob()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Jobs.Add(NewJob("j1", "Patrol", "creator", j => j.CaseNumber = "FIB-A-2026-0007"));
            db.SaveChanges();
        }
        var (svc, _) = Build(ctx);

        var display = await svc.ReferenceDisplayAsync(nameof(Job), "j1", Leader());

        Assert.Equal("Patrol (FIB-A-2026-0007)", display);
    }

    [Fact]
    public async Task ReferenceDisplayAsync_ReturnsNull_ForBlankType()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var display = await svc.ReferenceDisplayAsync("  ", "j1", Leader());

        Assert.Null(display);
    }
}
