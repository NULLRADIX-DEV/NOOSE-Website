using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Taskforces;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Taskforces;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TaskforceService"/> against in-memory SQLite.</summary>
public sealed class TaskforceServiceTests
{
    // Rank >= SupervisorySpecialAgent(4) or admin => IsLeadership().
    private static ClaimsPrincipal Leader(string id = "lead")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).Build();

    // Junior agent: not leadership, not admin.
    private static ClaimsPrincipal Member(string id)
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.JuniorAgent).Build();

    private static TaskforceService NewService(SqliteTestContext ctx)
    {
        var caseNo = Substitute.For<ICaseNumberService>();
        caseNo.NextAsync(Arg.Any<AppDbContext>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("NOOSE-TF-2026-0001");
        return new TaskforceService(ctx.Factory, caseNo, Substitute.For<INotificationService>());
    }

    private static Taskforce Tf(string id, string name = "Alpha TF", Action<Taskforce>? cfg = null)
    {
        var t = new Taskforce
        {
            Id = id,
            CaseNumber = $"NOOSE-TF-2026-{id}",
            Name = name,
            Status = TaskforceStatus.Approved,
            Scope = TaskforceScope.InternalAgency,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        cfg?.Invoke(t);
        return t;
    }

    private static TaskforceAgent Alloc(string taskforceId, string agentId, TaskforceRole role = TaskforceRole.Member, string? id = null)
        => new()
        {
            Id = id ?? Guid.NewGuid().ToString(),
            TaskforceId = taskforceId,
            AgentId = agentId,
            Role = role,
        };

    // ---- GetListAsync ----

    [Fact]
    public async Task GetListAsync_MayAll_ReturnsAll_OrderedByModifiedThenCreated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
            db.Taskforces.Add(Tf("t2", cfg: t => t.CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc)));
            db.Taskforces.Add(Tf("t3", cfg: t =>
            {
                t.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                t.ModifiedAt = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(true, null);

        // sort key = ModifiedAt ?? CreatedAt, descending: t3 (Mar) > t2 (Jan2) > t1 (Jan1).
        Assert.Equal(new[] { "t3", "t2", "t1" }, result.Select(t => t.Id).ToArray());
    }

    [Fact]
    public async Task GetListAsync_NonLeader_ReturnsOnlyAssigned()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Taskforces.Add(Tf("t2"));
            db.TaskforceAgents.Add(Alloc("t1", "m1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(false, "m1");

        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task GetListAsync_Partner_ReturnsOnlyReleasedNonClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));                                  // released, not classified -> visible
            db.Taskforces.Add(Tf("t2", cfg: t => t.IsClassified = true)); // released but classified -> hidden
            db.Taskforces.Add(Tf("t3"));                                  // not released -> hidden
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Taskforce), EntityId = "t1", Agency = PartnerAgency.DoJ });
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Taskforce), EntityId = "t2", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetListAsync(false, null, partnerAgency: PartnerAgency.DoJ);

        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    // ---- GetDetailAsync ----

    [Fact]
    public async Task GetDetailAsync_MayAll_ReturnsTaskforce()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("t1", true, null);

        Assert.NotNull(result);
        Assert.Equal("t1", result!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_NonMember_ReturnsNull()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("t1", false, "outsider");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDetailAsync_Member_ReturnsTaskforce()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("t1", false, "m1");

        Assert.NotNull(result);
        Assert.Equal("t1", result!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_Partner_ReturnsReleasedNonClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Taskforce), EntityId = "t1", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("t1", false, null, partnerAgency: PartnerAgency.DoJ);

        Assert.NotNull(result);
        Assert.Equal("t1", result!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_Partner_ReturnsNull_WhenClassified()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.IsClassified = true));
            db.PartnerShares.Add(new PartnerShare { EntityType = nameof(Taskforce), EntityId = "t1", Agency = PartnerAgency.DoJ });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetDetailAsync("t1", false, null, partnerAgency: PartnerAgency.DoJ);

        Assert.Null(result);
    }

    // ---- GetTrashAsync ----

    [Fact]
    public async Task GetTrashAsync_ReturnsSoftDeleted_OrderedByDeletedAtDesc()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t =>
            {
                t.IsDeleted = true;
                t.DeletedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Taskforces.Add(Tf("t2", cfg: t =>
            {
                t.IsDeleted = true;
                t.DeletedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Taskforces.Add(Tf("t3")); // live -> excluded
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetTrashAsync();

        Assert.Equal(new[] { "t2", "t1" }, result.Select(t => t.Id).ToArray());
    }

    // ---- SearchAsync ----

    [Fact]
    public async Task SearchAsync_FiltersByNameSubstring()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", name: "Bravo Team"));
            db.Taskforces.Add(Tf("t2", name: "Charlie Unit"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("rav", true, null);

        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task SearchAsync_FiltersByCaseNumber()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.CaseNumber = "NOOSE-TF-SPECIAL"));
            db.Taskforces.Add(Tf("t2", cfg: t => t.CaseNumber = "NOOSE-TF-OTHER"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync("SPECIAL", true, null);

        Assert.Single(result);
        Assert.Equal("t1", result[0].Id);
    }

    [Fact]
    public async Task SearchAsync_EmptyText_ReturnsAll_OrderedByName_RespectingMax()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", name: "Zulu"));
            db.Taskforces.Add(Tf("t2", name: "Alpha"));
            db.Taskforces.Add(Tf("t3", name: "Mike"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.SearchAsync(null, true, null, max: 2);

        // ordered by name, capped at 2: Alpha, Mike.
        Assert.Equal(new[] { "Alpha", "Mike" }, result.Select(t => t.Name).ToArray());
    }

    // ---- GetRequestedAsync ----

    [Fact]
    public async Task GetRequestedAsync_ReturnsOnlyRequested_OrderedByCreated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t =>
            {
                t.Status = TaskforceStatus.Requested;
                t.CreatedAt = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.Taskforces.Add(Tf("t2", cfg: t => t.Status = TaskforceStatus.Approved));
            db.Taskforces.Add(Tf("t3", cfg: t =>
            {
                t.Status = TaskforceStatus.Requested;
                t.CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetRequestedAsync();

        // requested only, oldest first: t3 (Jan1) then t1 (Jan2).
        Assert.Equal(new[] { "t3", "t1" }, result.Select(t => t.Id).ToArray());
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_Persists_AndAutoAssignsCreatorAsLead()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new TaskforceInput
        {
            Name = "  Task Alpha  ",
            Purpose = "Investigate",
            Scope = TaskforceScope.CrossAgency,
            IsClassified = true,
        };

        var created = await svc.CreateAsync(input, Leader("creator"));

        Assert.Equal("NOOSE-TF-2026-0001", created.CaseNumber);
        Assert.Equal("Task Alpha", created.Name);
        Assert.Equal(TaskforceStatus.Requested, created.Status);
        Assert.True(created.IsClassified);

        using var db = ctx.NewContext();
        var stored = await db.Taskforces.SingleAsync(t => t.Id == created.Id);
        Assert.Equal("Task Alpha", stored.Name);
        var lead = await db.TaskforceAgents.SingleAsync(a => a.TaskforceId == created.Id);
        Assert.Equal("creator", lead.AgentId);
        Assert.Equal(TaskforceRole.LeadInvestigator, lead.Role);
    }

    [Fact]
    public async Task CreateAsync_TrimsName_AndNullsBlankOptionalFields()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new TaskforceInput { Name = "  Task  ", Purpose = "   ", Remarks = "" };

        var created = await svc.CreateAsync(input, Leader("creator"));

        Assert.Equal("Task", created.Name);
        Assert.Null(created.Purpose);
        Assert.Null(created.Remarks);
    }

    [Fact]
    public async Task CreateAsync_Anonymous_NoLeadAssigned()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new TaskforceInput { Name = "Task" };

        var created = await svc.CreateAsync(input, ClaimsPrincipalBuilder.Anonymous());

        using var db = ctx.NewContext();
        Assert.True(await db.Taskforces.AnyAsync(t => t.Id == created.Id));
        // no agent id on the actor -> no auto-lead row.
        Assert.False(await db.TaskforceAgents.AnyAsync(a => a.TaskforceId == created.Id));
    }

    // ---- RefreshAsync ----

    [Fact]
    public async Task RefreshAsync_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", name: "Old"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new TaskforceInput
        {
            Name = "  New  ",
            Purpose = "Reason",
            Scope = TaskforceScope.CrossAgency,
            Remarks = "Note",
            IsClassified = true,
        };

        await svc.RefreshAsync("t1", input, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Taskforces.SingleAsync(t => t.Id == "t1");
        Assert.Equal("New", stored.Name);
        Assert.Equal("Reason", stored.Purpose);
        Assert.Equal(TaskforceScope.CrossAgency, stored.Scope);
        Assert.Equal("Note", stored.Remarks);
        Assert.True(stored.IsClassified);
    }

    [Fact]
    public async Task RefreshAsync_Throws_WhenClassified_AndNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.IsClassified = true));
            db.SaveChanges();
        }
        var svc = NewService(ctx);
        var input = new TaskforceInput { Name = "New" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync("t1", input, Member("m1")));
    }

    [Fact]
    public async Task RefreshAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);
        var input = new TaskforceInput { Name = "New" };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", input, Leader()));
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task DeleteAsync_RemovesTaskforce()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.DeleteAsync("t1", "Dublette", Leader());

        // no soft-delete interceptor in tests => hard delete: gone even ignoring filters.
        using var check = ctx.NewContext();
        Assert.False(await check.Taskforces.IgnoreQueryFilters().AnyAsync(t => t.Id == "t1"));
    }

    [Fact]
    public async Task DeleteAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync("any", "Dublette", Member("m1")));
    }

    [Fact]
    public async Task DeleteAsync_Throws_OnUnknownId()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.DeleteAsync("missing", "Dublette", Leader()));
    }

    // ---- RestoreAsync ----

    [Fact]
    public async Task RestoreAsync_ClearsSoftDeleteFlags()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t =>
            {
                t.IsDeleted = true;
                t.DeletedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                t.DeletedById = "someone";
            }));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RestoreAsync("t1", Leader());

        using var check = ctx.NewContext();
        // reappears in the filtered set with the flags cleared.
        var stored = await check.Taskforces.SingleAsync(t => t.Id == "t1");
        Assert.False(stored.IsDeleted);
        Assert.Null(stored.DeletedAt);
        Assert.Null(stored.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync("any", Member("m1")));
    }

    // ---- ApprovalSetAsync ----

    [Fact]
    public async Task ApprovalSetAsync_UpdatesStatus()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.Status = TaskforceStatus.Requested));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.ApprovalSetAsync("t1", TaskforceStatus.Approved, Leader());

        using var check = ctx.NewContext();
        var stored = await check.Taskforces.SingleAsync(t => t.Id == "t1");
        Assert.Equal(TaskforceStatus.Approved, stored.Status);
    }

    [Fact]
    public async Task ApprovalSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.ApprovalSetAsync("any", TaskforceStatus.Approved, Member("m1")));
    }

    // ---- GetAgentsAsync ----

    [Fact]
    public async Task GetAgentsAsync_ReturnsAssigned_LeadsFirst()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("aL", configure: a => a.Codename = "Mike"));
            db.Users.Add(Seed.Agent("aM1", configure: a => a.Codename = "Zulu"));
            db.Users.Add(Seed.Agent("aM2", configure: a => a.Codename = "Alpha"));
            db.TaskforceAgents.Add(Alloc("t1", "aL", TaskforceRole.LeadInvestigator));
            db.TaskforceAgents.Add(Alloc("t1", "aM1", TaskforceRole.Member));
            db.TaskforceAgents.Add(Alloc("t1", "aM2", TaskforceRole.Member));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetAgentsAsync("t1");

        // lead first, then members ordered by codename (Alpha < Zulu).
        Assert.Equal(new[] { "aL", "aM2", "aM1" }, result.Select(a => a.AgentId).ToArray());
    }

    // ---- GetLeadAsync ----

    [Fact]
    public async Task GetLeadAsync_ReturnsOnlyLeads()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("aL", configure: a => a.Codename = "Mike"));
            db.Users.Add(Seed.Agent("aM", configure: a => a.Codename = "Zulu"));
            db.TaskforceAgents.Add(Alloc("t1", "aL", TaskforceRole.LeadInvestigator));
            db.TaskforceAgents.Add(Alloc("t1", "aM", TaskforceRole.Member));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetLeadAsync("t1");

        Assert.Single(result);
        Assert.Equal("aL", result[0].AgentId);
    }

    // ---- AgentAllocateAsync ----

    [Fact]
    public async Task AgentAllocateAsync_AddsMember()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentAllocateAsync("t1", "target", Leader());

        using var check = ctx.NewContext();
        var alloc = await check.TaskforceAgents.SingleAsync(a => a.TaskforceId == "t1" && a.AgentId == "target");
        Assert.Equal(TaskforceRole.Member, alloc.Role);
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(NotSelectable(id));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // partner read access comes from PartnerShare, never from a membership row
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("t1", id, Leader()));
    }

    /// <summary>An agent shape no picker offers; the service must reject it too.</summary>
    private static NOOSE_Website.Data.Entities.Agent NotSelectable(string id) => id switch
    {
        "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
        "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
        "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
        _ => Seed.Agent(id, status: AgentStatus.Terminated),
    };

    [Fact]
    public async Task AgentAllocateAsync_AllowsTaskforceLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("target"));
            // actor is a junior-rank agent but a lead of this taskforce.
            db.TaskforceAgents.Add(Alloc("t1", "leadmember", TaskforceRole.LeadInvestigator));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentAllocateAsync("t1", "target", Member("leadmember"));

        using var check = ctx.NewContext();
        Assert.True(await check.TaskforceAgents.AnyAsync(a => a.TaskforceId == "t1" && a.AgentId == "target"));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenClassified_AndNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.IsClassified = true));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("t1", "target", Member("m1")));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAgentNotFound()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("t1", "ghost", Leader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenAlreadyAllocated()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("target"));
            db.TaskforceAgents.Add(Alloc("t1", "target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.AgentAllocateAsync("t1", "target", Leader()));
    }

    [Fact]
    public async Task AgentAllocateAsync_Throws_WhenNotLeadershipOrLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.Users.Add(Seed.Agent("target"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentAllocateAsync("t1", "target", Member("outsider")));
    }

    // ---- AgentRemoveAsync ----

    [Fact]
    public async Task AgentRemoveAsync_RemovesAllocation()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1", id: "alloc1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.AgentRemoveAsync("alloc1", Leader());

        using var check = ctx.NewContext();
        Assert.False(await check.TaskforceAgents.AnyAsync(a => a.Id == "alloc1"));
    }

    [Fact]
    public async Task AgentRemoveAsync_NoOp_WhenNotFound()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        // returns quietly when the allocation does not exist.
        await svc.AgentRemoveAsync("missing", Leader());

        using var check = ctx.NewContext();
        Assert.Equal(0, await check.TaskforceAgents.CountAsync());
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenClassified_AndNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1", cfg: t => t.IsClassified = true));
            db.TaskforceAgents.Add(Alloc("t1", "m1", id: "alloc1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync("alloc1", Member("m1")));
    }

    [Fact]
    public async Task AgentRemoveAsync_Throws_WhenNotLeadershipOrLead()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1", id: "alloc1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AgentRemoveAsync("alloc1", Member("outsider")));
    }

    // ---- RoleSetAsync ----

    [Fact]
    public async Task RoleSetAsync_UpdatesRole()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1", TaskforceRole.Member, id: "alloc1"));
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        await svc.RoleSetAsync("alloc1", TaskforceRole.CidLead, Leader());

        using var check = ctx.NewContext();
        var stored = await check.TaskforceAgents.SingleAsync(a => a.Id == "alloc1");
        Assert.Equal(TaskforceRole.CidLead, stored.Role);
    }

    [Fact]
    public async Task RoleSetAsync_Throws_WhenNotLeadership()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RoleSetAsync("any", TaskforceRole.CidLead, Member("m1")));
    }

    [Fact]
    public async Task RoleSetAsync_Throws_OnUnknownAllocation()
    {
        using var ctx = new SqliteTestContext();
        var svc = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RoleSetAsync("missing", TaskforceRole.CidLead, Leader()));
    }

    // ---- GetHistoryAsync ----

    [Fact]
    public async Task GetHistoryAsync_ReturnsRelevantAuditLogs_WhenVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.TaskforceAgents.Add(Alloc("t1", "m1", id: "alloc1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Taskforce),
                EntityId = "t1",
                Timestamp = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            });
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(TaskforceAgent),
                EntityId = "alloc1",
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            // unrelated entry must be excluded.
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = "Person",
                EntityId = "p1",
                Timestamp = new DateTime(2026, 1, 3, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        var result = await svc.GetHistoryAsync("t1", true, null);

        // taskforce + allocation entries, newest first; Person entry excluded.
        Assert.Equal(2, result.Count);
        Assert.Equal(nameof(Taskforce), result[0].EntityType);
        Assert.Equal(nameof(TaskforceAgent), result[1].EntityType);
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsEmpty_WhenNotVisible()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Taskforces.Add(Tf("t1"));
            db.AuditLogs.Add(new AuditLog
            {
                EntityType = nameof(Taskforce),
                EntityId = "t1",
                Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            });
            db.SaveChanges();
        }
        var svc = NewService(ctx);

        // non-leader, not a member -> not visible.
        var result = await svc.GetHistoryAsync("t1", false, null);

        Assert.Empty(result);
    }
}
