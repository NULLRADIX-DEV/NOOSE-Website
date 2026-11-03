using System.Security.Claims;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="TrainingModuleService"/> against in-memory SQLite.</summary>
public sealed class TrainingModuleServiceTests
{
    // Rank >= SpecialAgentInCharge(4) OR admin => IsLeadership() => passes RequireLeadership.
    private static ClaimsPrincipal Leader(string id = "lead", string codename = "Falcon")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename(codename).Build();

    // Junior agent: not leadership, not admin => fails RequireLeadership.
    private static ClaimsPrincipal NonLeader(string id = "plain")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).Build();

    // HRB is rank-independent: this account may tick a module off but may not administer the catalogue.
    private static ClaimsPrincipal JuniorHrb(string id = "hrb")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Trainee).WithCodename("Sparrow").AsHrb().Build();

    // Read-only supervision: reads everything, writes nothing. Leadership by rank, refused by the write check.
    private static ClaimsPrincipal OnlyReader(string id = "aufsicht")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.Director).WithCodename("Heron").AsTeamLead().Build();

    private static TrainingModuleService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    private static TrainingModule Mod(string id, string name, int sorting = 0, bool active = true)
        => new()
        {
            Id = id,
            Name = name,
            Sorting = sorting,
            IsActive = active,
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    private static AgentModuleCompletion Comp(string id, string agentId, string moduleId)
        => new()
        {
            Id = id,
            AgentId = agentId,
            ModuleId = moduleId,
            CompletedAt = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };

    // ---- GetAllAsync ----

    [Fact]
    public async Task GetAllAsync_ReturnsActiveAndInactive_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m3", "Zebra", sorting: 2, active: false));
            db.TrainingModules.Add(Mod("m1", "Alpha", sorting: 1, active: true));
            db.TrainingModules.Add(Mod("m2", "Beta", sorting: 1, active: true));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetAllAsync();

        Assert.Equal(new[] { "m1", "m2", "m3" }, result.Select(m => m.Id).ToArray());
    }

    // ---- GetActiveAsync ----

    [Fact]
    public async Task GetActiveAsync_ReturnsOnlyActive_OrderedBySortingThenName()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha", sorting: 1, active: true));
            db.TrainingModules.Add(Mod("m2", "Beta", sorting: 2, active: false));
            db.TrainingModules.Add(Mod("m3", "Gamma", sorting: 3, active: true));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetActiveAsync();

        Assert.Equal(new[] { "m1", "m3" }, result.Select(m => m.Id).ToArray());
    }

    // ---- CreateAsync ----

    [Fact]
    public async Task CreateAsync_AsLeader_TrimsAndPersists()
    {
        using var ctx = new SqliteTestContext();

        var created = await NewService(ctx).CreateAsync(
            new ModuleInput { Name = "  Modul A  ", Description = "  desc  ", IsActive = true, Sorting = 5 },
            Leader());

        Assert.Equal("Modul A", created.Name);
        Assert.Equal("desc", created.Description);

        using var db = ctx.NewContext();
        var stored = Assert.Single(db.TrainingModules.ToList());
        Assert.Equal("Modul A", stored.Name);
        Assert.Equal("desc", stored.Description);
        Assert.True(stored.IsActive);
        Assert.Equal(5, stored.Sorting);
    }

    [Fact]
    public async Task CreateAsync_BlankDescription_StoredAsNull()
    {
        using var ctx = new SqliteTestContext();

        var created = await NewService(ctx).CreateAsync(
            new ModuleInput { Name = "Modul B", Description = "   ", IsActive = false, Sorting = 0 },
            Leader());

        Assert.Null(created.Description);
        Assert.False(created.IsActive);
    }

    [Fact]
    public async Task CreateAsync_EmptyName_Throws()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).CreateAsync(new ModuleInput { Name = "   " }, Leader()));
    }

    [Fact]
    public async Task CreateAsync_DuplicateName_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Existing"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).CreateAsync(new ModuleInput { Name = "  Existing  " }, Leader()));
    }

    [Fact]
    public async Task CreateAsync_AsNonLeader_ThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).CreateAsync(new ModuleInput { Name = "Modul" }, NonLeader()));
    }

    // ---- UpdateAsync ----

    [Fact]
    public async Task UpdateAsync_AsLeader_UpdatesFields()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Old", sorting: 1, active: true));
            db.SaveChanges();
        }

        await NewService(ctx).UpdateAsync("m1",
            new ModuleInput { Name = "  New  ", Description = "note", IsActive = false, Sorting = 9 },
            Leader());

        using var check = ctx.NewContext();
        var stored = check.TrainingModules.Single(m => m.Id == "m1");
        Assert.Equal("New", stored.Name);
        Assert.Equal("note", stored.Description);
        Assert.False(stored.IsActive);
        Assert.Equal(9, stored.Sorting);
    }

    [Fact]
    public async Task UpdateAsync_ModuleNotFound_Throws()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateAsync("nope", new ModuleInput { Name = "Any" }, Leader()));
    }

    [Fact]
    public async Task UpdateAsync_DuplicateNameOnOtherModule_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.TrainingModules.Add(Mod("m2", "Beta"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateAsync("m2", new ModuleInput { Name = "Alpha" }, Leader()));
    }

    [Fact]
    public async Task UpdateAsync_EmptyName_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).UpdateAsync("m1", new ModuleInput { Name = "  " }, Leader()));
    }

    [Fact]
    public async Task UpdateAsync_AsNonLeader_ThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).UpdateAsync("m1", new ModuleInput { Name = "New" }, NonLeader()));
    }

    // ---- DeleteAsync ----

    [Fact]
    public async Task DeleteAsync_AsLeader_RemovesModuleAndCompletions()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.AgentModuleCompletions.Add(Comp("c1", "a1", "m1"));
            db.AgentModuleCompletions.Add(Comp("c2", "a2", "m1"));
            db.SaveChanges();
        }

        await NewService(ctx).DeleteAsync("m1", Leader());

        using var check = ctx.NewContext();
        Assert.Empty(check.TrainingModules.ToList());
        Assert.Empty(check.AgentModuleCompletions.ToList());
    }

    [Fact]
    public async Task DeleteAsync_MissingModule_NoThrow()
    {
        using var ctx = new SqliteTestContext();

        // no-op: module not found returns silently
        await NewService(ctx).DeleteAsync("ghost", Leader());
    }

    [Fact]
    public async Task DeleteAsync_AsNonLeader_ThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).DeleteAsync("m1", NonLeader()));
    }

    // ---- GetStatusForAgentAsync ----

    [Fact]
    public async Task GetStatusForAgentAsync_MergesActiveAndCompletedHistory_Ordered()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha", sorting: 1, active: true));
            db.TrainingModules.Add(Mod("m2", "Beta", sorting: 2, active: true));
            db.TrainingModules.Add(Mod("m3", "Gamma", sorting: 3, active: false));
            // completed active module + completed now-inactive module (kept as history)
            db.AgentModuleCompletions.Add(Comp("c1", "a1", "m2"));
            db.AgentModuleCompletions.Add(Comp("c2", "a1", "m3"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetStatusForAgentAsync("a1");

        Assert.Equal(new[] { "m1", "m2", "m3" }, result.Select(s => s.Module.Id).ToArray());
        Assert.False(result[0].IsCompleted);   // m1 active, not completed
        Assert.True(result[1].IsCompleted);    // m2 active, completed
        Assert.True(result[2].IsCompleted);    // m3 inactive, completed -> history
        Assert.Equal("c1", result[1].CompletionId);
    }

    [Fact]
    public async Task GetStatusForAgentAsync_IgnoresOtherAgentsCompletions()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha", sorting: 1, active: true));
            db.AgentModuleCompletions.Add(Comp("c1", "other", "m1"));
            db.SaveChanges();
        }

        var result = await NewService(ctx).GetStatusForAgentAsync("a1");

        var only = Assert.Single(result);
        Assert.Equal("m1", only.Module.Id);
        Assert.False(only.IsCompleted);
    }

    // ---- MarkCompletedAsync ----

    [Fact]
    public async Task MarkCompletedAsync_AsLeader_CreatesCompletionWithActorCodename()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        var completion = await NewService(ctx).MarkCompletedAsync("a1", "m1", "  well done  ", Leader());

        Assert.Equal("a1", completion.AgentId);
        Assert.Equal("m1", completion.ModuleId);
        Assert.Equal("Falcon", completion.CompleterName);
        Assert.Equal("well done", completion.Note);
        Assert.NotEqual(default, completion.CompletedAt);

        using var check = ctx.NewContext();
        var stored = Assert.Single(check.AgentModuleCompletions.ToList());
        Assert.Equal("a1", stored.AgentId);
        Assert.Equal("m1", stored.ModuleId);
    }

    [Fact]
    public async Task MarkCompletedAsync_UnknownAgent_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).MarkCompletedAsync("ghost", "m1", null, Leader()));
    }

    [Theory]
    [InlineData("teamlead")]
    [InlineData("teamlead-admin")]
    [InlineData("partner")]
    [InlineData("terminated")]
    public async Task MarkCompletedAsync_Throws_WhenAgentNotSelectable(string id)
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.Users.Add(id switch
            {
                "teamlead" => Seed.Agent(id, configure: a => a.IsTeamLead = true),
                "teamlead-admin" => Seed.Agent(id, configure: a => { a.IsTeamLead = true; a.IsAdmin = true; }),
                "partner" => Seed.Agent(id, configure: a => a.PartnerAgency = PartnerAgency.LSPD),
                _ => Seed.Agent(id, status: AgentStatus.Terminated),
            });
            db.SaveChanges();
        }

        // the training catalogue is NOOSE-internal
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).MarkCompletedAsync(id, "m1", null, Leader()));
    }

    [Fact]
    public async Task MarkCompletedAsync_UnknownModule_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).MarkCompletedAsync("a1", "ghost", null, Leader()));
    }

    [Fact]
    public async Task MarkCompletedAsync_AlreadyCompleted_Throws()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.AgentModuleCompletions.Add(Comp("c1", "a1", "m1"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            NewService(ctx).MarkCompletedAsync("a1", "m1", null, Leader()));
    }

    [Fact]
    public async Task MarkCompletedAsync_AsNonLeader_ThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).MarkCompletedAsync("a1", "m1", null, NonLeader()));
    }

    // ---- UnmarkCompletedAsync ----

    [Fact]
    public async Task UnmarkCompletedAsync_AsLeader_RemovesCompletion()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentModuleCompletions.Add(Comp("c1", "a1", "m1"));
            db.SaveChanges();
        }

        await NewService(ctx).UnmarkCompletedAsync("c1", Leader());

        using var check = ctx.NewContext();
        Assert.Empty(check.AgentModuleCompletions.ToList());
    }

    [Fact]
    public async Task UnmarkCompletedAsync_MissingCompletion_NoThrow()
    {
        using var ctx = new SqliteTestContext();

        // no-op: completion not found returns silently
        await NewService(ctx).UnmarkCompletedAsync("ghost", Leader());
    }

    [Fact]
    public async Task UnmarkCompletedAsync_AsNonLeader_ThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).UnmarkCompletedAsync("c1", NonLeader()));
    }

    // ---- who may tick: HRB or leadership, and nobody else ----

    /// <summary>The point of the change: the HRB flag carries no rank, and it does not need one here.</summary>
    [Fact]
    public async Task MarkCompletedAsync_AsHrbWithoutRank_Succeeds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        var completion = await NewService(ctx).MarkCompletedAsync("a1", "m1", null, JuniorHrb());

        Assert.Equal("Sparrow", completion.CompleterName);
        using var check = ctx.NewContext();
        Assert.Single(check.AgentModuleCompletions.ToList());
    }

    [Fact]
    public async Task UnmarkCompletedAsync_AsHrbWithoutRank_Succeeds()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.AgentModuleCompletions.Add(Comp("c1", "a1", "m1"));
            db.SaveChanges();
        }

        await NewService(ctx).UnmarkCompletedAsync("c1", JuniorHrb());

        using var check = ctx.NewContext();
        Assert.Empty(check.AgentModuleCompletions.ToList());
    }

    /// <summary>A plain agent without the flag still cannot tick - widening HRB must not widen everyone.</summary>
    [Fact]
    public async Task MarkCompletedAsync_AsPlainAgent_StillThrowsUnauthorized()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).MarkCompletedAsync("a1", "m1", null,
                ClaimsPrincipalBuilder.Agent("plain").WithRank(Rank.SeniorSpecialAgent).Build()));
    }

    /// <summary>Write check before rank check: the supervision is leadership by rank and still refused.</summary>
    [Fact]
    public async Task MarkCompletedAsync_AsReadOnlySupervision_IsRefusedBeforeAnythingIsWritten()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a1"));
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            NewService(ctx).MarkCompletedAsync("a1", "m1", null, OnlyReader()));

        using var check = ctx.NewContext();
        Assert.Empty(check.AgentModuleCompletions.ToList());
    }

    /// <summary>The split that is the whole point: ticking widened, administering the catalogue did not.</summary>
    [Fact]
    public async Task Administering_the_catalogue_stays_with_leadership()
    {
        using var ctx = new SqliteTestContext();
        using (var db = ctx.NewContext())
        {
            db.TrainingModules.Add(Mod("m1", "Alpha"));
            db.SaveChanges();
        }

        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.CreateAsync(new ModuleInput { Name = "Neu" }, JuniorHrb()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.UpdateAsync("m1", new ModuleInput { Name = "Beta" }, JuniorHrb()));
        // DeleteAsync also wipes every agent's tick history for that module
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.DeleteAsync("m1", JuniorHrb()));
    }
}
