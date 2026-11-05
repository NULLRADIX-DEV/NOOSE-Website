using System.Security.Claims;
using NOOSE_Website.Data.Entities;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Models.Absences;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AbsenceService"/> over in-memory SQLite.</summary>
public class AbsenceServiceTests
{
    // The soft-delete rewrite interceptor is DI-only and absent here, so DeleteAsync HARD-deletes.

    private static (AbsenceService Svc, INotificationService Notifications) Build(SqliteTestContext ctx)
    {
        var notifications = Substitute.For<INotificationService>();
        return (new AbsenceService(ctx.Factory, notifications), notifications);
    }

    private static DateOnly Today => DateOnly.FromDateTime(MeetingTime.Local(DateTime.UtcNow));

    private static DateTime AsInput(DateOnly day) => day.ToDateTime(TimeOnly.MinValue);

    private static Absence SeedAbsence(SqliteTestContext ctx, string agentId, DateOnly from, DateOnly to,
        Action<Absence>? configure = null)
    {
        var abs = new Absence
        {
            AgentId = agentId,
            FromDate = from,
            ToDate = to,
            Days = to.DayNumber - from.DayNumber + 1,
            Category = AbsenceCategory.Vacation,
            Reason = "geheim",
        };
        configure?.Invoke(abs);

        using var db = ctx.NewContext();
        if (!db.Users.Any(u => u.Id == agentId))
        {
            db.Users.Add(Seed.Agent(agentId));
        }
        db.Absences.Add(abs);
        db.SaveChanges();
        return abs;
    }

    /// <summary>Creates the agent row up front so the absence seeder does not fall back to defaults.</summary>
    private static void SeedAgent(SqliteTestContext ctx, string id, Action<Agent> configure)
    {
        using var db = ctx.NewContext();
        if (db.Users.Any(u => u.Id == id))
        {
            return;
        }
        db.Users.Add(Seed.Agent(id, configure: configure));
        db.SaveChanges();
    }

    private static ClaimsPrincipal Owner(string id) => ClaimsPrincipalBuilder.Agent(id).Build();

    private static ClaimsPrincipal Leader(string id = "lead", string codename = "Chief")
        => ClaimsPrincipalBuilder.Agent(id).WithRank(Rank.SectionChief).WithCodename(codename).Build();

    private static ClaimsPrincipal OnlyReader(string id = "tl")
        => ClaimsPrincipalBuilder.Agent(id).AsTeamLead().Build();

    // ---------- GetListAsync ----------

    [Fact]
    public async Task GetListAsync_OwnScope_ReturnsOnlyOwn()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "a2", Today.AddDays(1), Today.AddDays(3));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Own);

        var row = Assert.Single(rows);
        Assert.Equal("a1", row.AgentId);
        Assert.Equal("Codename-a1", row.Codename);
        Assert.Equal("geheim", row.Reason); // own reason is visible
    }

    [Fact]
    public async Task GetListAsync_AllScope_ReturnsAll_NewestFirst()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(2));   // later FromDate
        SeedAbsence(ctx, "a2", Today.AddDays(-10), Today.AddDays(-8)); // earlier FromDate

        var rows = await svc.GetListAsync(Leader("someoneElse"), AbsenceViewScope.All);

        Assert.Equal(2, rows.Count);
        // ordered by FromDate descending
        Assert.Equal("a1", rows[0].AgentId);
        Assert.Equal("a2", rows[1].AgentId);
        // All => reason visible even for others, and MayEdit is always true
        Assert.All(rows, r => Assert.Equal("geheim", r.Reason));
        Assert.All(rows, r => Assert.True(r.MayEdit));
    }

    [Fact]
    public async Task GetListAsync_DateWindow_FiltersByOverlap()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var before = SeedAbsence(ctx, "a1", Today.AddDays(-20), Today.AddDays(-15));
        var overlap = SeedAbsence(ctx, "a1", Today.AddDays(-2), Today.AddDays(2));
        var after = SeedAbsence(ctx, "a1", Today.AddDays(15), Today.AddDays(20));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Own,
            from: Today.AddDays(-5), to: Today.AddDays(5));

        var row = Assert.Single(rows);
        Assert.Equal(overlap.Id, row.Id);
        Assert.DoesNotContain(rows, r => r.Id == before.Id);
        Assert.DoesNotContain(rows, r => r.Id == after.Id);
    }

    [Fact]
    public async Task GetListAsync_MayEdit_TrueForFutureOwn_FalseForPastOwn()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var past = SeedAbsence(ctx, "a1", Today.AddDays(-10), Today.AddDays(-5));
        var future = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Own);

        Assert.False(rows.Single(r => r.Id == past.Id).MayEdit);
        Assert.True(rows.Single(r => r.Id == future.Id).MayEdit);
    }

    // ---------- GetListAsync, team scope ----------

    [Fact]
    public async Task GetListAsync_TeamScope_IncludesRosterPeersAndOwn()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "a2", Today.AddDays(2), Today.AddDays(4));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.AgentId == "a1");
        Assert.Contains(rows, r => r.AgentId == "a2");
        Assert.Contains(rows, r => r.Codename == "Codename-a2");
    }

    [Fact]
    public async Task GetListAsync_TeamScope_RedactsForeignReason_KeepsOwn()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "a2", Today.AddDays(2), Today.AddDays(4));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team);

        Assert.Equal("geheim", rows.Single(r => r.AgentId == "a1").Reason);
        Assert.Null(rows.Single(r => r.AgentId == "a2").Reason);
    }

    [Fact]
    public async Task GetListAsync_TeamScope_RedactsForeignAcknowledgement()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        void Acknowledged(Absence a)
        {
            a.AcknowledgedAt = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            a.AcknowledgedById = "lead";
            a.AcknowledgedByName = "Chief";
        }

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3), Acknowledged);
        SeedAbsence(ctx, "a2", Today.AddDays(2), Today.AddDays(4), Acknowledged);

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team);

        var own = rows.Single(r => r.AgentId == "a1");
        Assert.NotNull(own.AcknowledgedAt);
        Assert.Equal("Chief", own.AcknowledgedByName);

        var foreign = rows.Single(r => r.AgentId == "a2");
        Assert.Null(foreign.AcknowledgedAt);
        Assert.Null(foreign.AcknowledgedByName);
    }

    [Fact]
    public async Task GetListAsync_TeamScope_MayEditFalseForForeignRows()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "a2", Today.AddDays(2), Today.AddDays(4));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team);

        Assert.True(rows.Single(r => r.AgentId == "a1").MayEdit);
        Assert.False(rows.Single(r => r.AgentId == "a2").MayEdit);
    }

    [Fact]
    public async Task GetListAsync_TeamScope_ExcludesTeamLeadBlockedAndPartnerAgents()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAgent(ctx, "tl", a => a.IsTeamLead = true);
        SeedAgent(ctx, "blocked", a => a.Status = AgentStatus.Blocked);
        SeedAgent(ctx, "partner", a => a.PartnerAgency = PartnerAgency.LSPD);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "tl", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "blocked", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "partner", Today.AddDays(1), Today.AddDays(3));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team);

        var row = Assert.Single(rows);
        Assert.Equal("a1", row.AgentId);
    }

    [Fact]
    public async Task GetListAsync_TeamScope_IncludesOwnRowEvenWhenOffRoster()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAgent(ctx, "tl", a => a.IsTeamLead = true);
        SeedAbsence(ctx, "tl", Today.AddDays(1), Today.AddDays(3));

        var rows = await svc.GetListAsync(Owner("tl"), AbsenceViewScope.Team);

        var row = Assert.Single(rows);
        Assert.Equal("tl", row.AgentId);
        Assert.Equal("geheim", row.Reason); // still the owner's own free text
    }

    [Fact]
    public async Task GetListAsync_TeamScope_FromWindow_DropsLapsedAbsences()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var lapsed = SeedAbsence(ctx, "a2", Today.AddDays(-10), Today.AddDays(-5));
        var running = SeedAbsence(ctx, "a2", Today.AddDays(-1), Today.AddDays(2));
        var upcoming = SeedAbsence(ctx, "a2", Today.AddDays(7), Today.AddDays(9));

        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.Team, from: Today);

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == running.Id);
        Assert.Contains(rows, r => r.Id == upcoming.Id);
        Assert.DoesNotContain(rows, r => r.Id == lapsed.Id);
    }

    [Fact]
    public async Task GetListAsync_RequestedAll_NonLeadership_ClampsToTeam()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAgent(ctx, "blocked", a => a.Status = AgentStatus.Blocked);
        SeedAbsence(ctx, "a2", Today.AddDays(1), Today.AddDays(3));
        SeedAbsence(ctx, "blocked", Today.AddDays(1), Today.AddDays(3));

        // a plain agent asking for everything gets team redaction, not leadership access
        var rows = await svc.GetListAsync(Owner("a1"), AbsenceViewScope.All);

        var row = Assert.Single(rows);
        Assert.Equal("a2", row.AgentId);
        Assert.Null(row.Reason);
        Assert.False(row.MayEdit);
    }

    [Fact]
    public async Task GetListAsync_RequestedAll_OnlyReader_KeepsReason()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(3));

        // read-only supervision reads classified content, so it is not clamped
        var rows = await svc.GetListAsync(OnlyReader(), AbsenceViewScope.All);

        Assert.Equal("geheim", Assert.Single(rows).Reason);
    }

    // ---------- GetDetailAsync ----------

    [Fact]
    public async Task GetDetailAsync_Own_ReturnsEntity()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today, Today.AddDays(2));

        var result = await svc.GetDetailAsync(abs.Id, Owner("a1"), AbsenceViewScope.Own);

        Assert.NotNull(result);
        Assert.Equal(abs.Id, result!.Id);
    }

    [Fact]
    public async Task GetDetailAsync_NotVisibleInOwnScope_ReturnsNull_ButAllScopeSees()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today, Today.AddDays(2));

        Assert.Null(await svc.GetDetailAsync(abs.Id, Owner("a2"), AbsenceViewScope.Own));
        Assert.NotNull(await svc.GetDetailAsync(abs.Id, Leader("a2"), AbsenceViewScope.All));
    }

    [Fact]
    public async Task GetDetailAsync_TeamScope_ReturnsForeignRosterRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today, Today.AddDays(2));

        Assert.NotNull(await svc.GetDetailAsync(abs.Id, Owner("a2"), AbsenceViewScope.Team));
    }

    // ---------- GetTrashAsync ----------

    [Fact]
    public async Task GetTrashAsync_ReturnsOnlyDeleted_WithAgent()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var deleted = SeedAbsence(ctx, "a1", Today, Today.AddDays(1),
            a => { a.IsDeleted = true; a.DeletedAt = DateTime.UtcNow; });
        SeedAbsence(ctx, "a2", Today, Today.AddDays(1)); // live, excluded

        var trash = await svc.GetTrashAsync();

        var row = Assert.Single(trash);
        Assert.Equal(deleted.Id, row.Id);
        Assert.NotNull(row.Agent);
        Assert.Equal("Codename-a1", row.Agent!.Codename);
    }

    // ---------- CreateAsync ----------

    [Fact]
    public async Task CreateAsync_PersistsAbsence_WithComputedDays()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new AbsenceInput
        {
            From = AsInput(Today.AddDays(1)),
            To = AsInput(Today.AddDays(4)),
            Category = AbsenceCategory.Sick,
            Reason = "  krank  ",
        };

        var created = await svc.CreateAsync(input, Owner("a1"));

        using var db = ctx.NewContext();
        var row = Assert.Single(db.Absences.ToList());
        Assert.Equal(created.Id, row.Id);
        Assert.Equal("a1", row.AgentId);
        Assert.Equal(Today.AddDays(1), row.FromDate);
        Assert.Equal(Today.AddDays(4), row.ToDate);
        Assert.Equal(4, row.Days); // inclusive span
        Assert.Equal(AbsenceCategory.Sick, row.Category);
        Assert.Equal("krank", row.Reason); // trimmed
    }

    [Fact]
    public async Task CreateAsync_OnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new AbsenceInput { From = AsInput(Today), To = AsInput(Today.AddDays(1)) };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, OnlyReader()));

        using var db = ctx.NewContext();
        Assert.Empty(db.Absences.ToList());
    }

    [Fact]
    public async Task CreateAsync_NoAgentContext_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        // Anonymous passes the write-access guard but has no agent id.
        var input = new AbsenceInput { From = AsInput(Today), To = AsInput(Today.AddDays(1)) };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.CreateAsync(input, ClaimsPrincipalBuilder.Anonymous()));
    }

    [Fact]
    public async Task CreateAsync_ToBeforeFrom_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new AbsenceInput { From = AsInput(Today.AddDays(5)), To = AsInput(Today.AddDays(1)) };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.CreateAsync(input, Owner("a1")));
    }

    // ---------- RefreshAsync ----------

    [Fact]
    public async Task RefreshAsync_Owner_UpdatesAndResetsAcknowledgement()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5), a =>
        {
            a.AcknowledgedAt = DateTime.UtcNow;
            a.AcknowledgedById = "someLead";
            a.AcknowledgedByName = "Lead";
            a.Reason = "old";
        });

        var input = new AbsenceInput
        {
            From = AsInput(Today.AddDays(2)),
            To = AsInput(Today.AddDays(10)),
            Category = AbsenceCategory.RpBreak,
            Reason = "  neu  ",
        };

        await svc.RefreshAsync(abs.Id, input, Owner("a1"));

        using var db = ctx.NewContext();
        var row = db.Absences.Single(a => a.Id == abs.Id);
        Assert.Equal(Today.AddDays(2), row.FromDate);
        Assert.Equal(Today.AddDays(10), row.ToDate);
        Assert.Equal(9, row.Days);
        Assert.Equal(AbsenceCategory.RpBreak, row.Category);
        Assert.Equal("neu", row.Reason);
        Assert.Null(row.AcknowledgedAt);
        Assert.Null(row.AcknowledgedById);
        Assert.Null(row.AcknowledgedByName);
    }

    [Fact]
    public async Task RefreshAsync_OnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));
        var input = new AbsenceInput { From = AsInput(Today.AddDays(1)), To = AsInput(Today.AddDays(5)) };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync(abs.Id, input, OnlyReader()));
    }

    [Fact]
    public async Task RefreshAsync_NonLeadershipNonOwner_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));
        var input = new AbsenceInput { From = AsInput(Today.AddDays(1)), To = AsInput(Today.AddDays(5)) };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RefreshAsync(abs.Id, input, Owner("a2")));
    }

    [Fact]
    public async Task RefreshAsync_NotFound_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var input = new AbsenceInput { From = AsInput(Today.AddDays(1)), To = AsInput(Today.AddDays(5)) };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => svc.RefreshAsync("missing", input, Owner("a1")));
    }

    // ---------- DeleteAsync ----------

    [Fact]
    public async Task DeleteAsync_Owner_HardDeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));

        await svc.DeleteAsync(abs.Id, Owner("a1"));

        using var db = ctx.NewContext();
        Assert.False(db.Absences.Any(a => a.Id == abs.Id));
        // no soft-delete interceptor in tests => physically gone, not just flagged
        Assert.False(db.Absences.IgnoreQueryFilters().Any(a => a.Id == abs.Id));
    }

    [Fact]
    public async Task DeleteAsync_OnlyReader_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.DeleteAsync(abs.Id, OnlyReader()));

        using var db = ctx.NewContext();
        Assert.True(db.Absences.Any(a => a.Id == abs.Id));
    }

    // ---------- RestoreAsync ----------

    [Fact]
    public async Task RestoreAsync_Leadership_UndeletesRow()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today, Today.AddDays(2),
            a => { a.IsDeleted = true; a.DeletedAt = DateTime.UtcNow; a.DeletedById = "x"; });

        await svc.RestoreAsync(abs.Id, Leader());

        using var db = ctx.NewContext();
        var row = db.Absences.Single(a => a.Id == abs.Id); // visible again in the filtered set
        Assert.False(row.IsDeleted);
        Assert.Null(row.DeletedAt);
        Assert.Null(row.DeletedById);
    }

    [Fact]
    public async Task RestoreAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today, Today.AddDays(2),
            a => { a.IsDeleted = true; a.DeletedAt = DateTime.UtcNow; });

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.RestoreAsync(abs.Id, Owner("a1")));

        using var db = ctx.NewContext();
        Assert.True(db.Absences.IgnoreQueryFilters().Single(a => a.Id == abs.Id).IsDeleted);
    }

    // ---------- AcknowledgeAsync ----------

    [Fact]
    public async Task AcknowledgeAsync_Leadership_SetsAcknowledgement()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));

        await svc.AcknowledgeAsync(abs.Id, Leader("lead", "Chief"));

        using var db = ctx.NewContext();
        var row = db.Absences.Single(a => a.Id == abs.Id);
        Assert.NotNull(row.AcknowledgedAt);
        Assert.Equal("lead", row.AcknowledgedById);
        Assert.Equal("Chief", row.AcknowledgedByName);
    }

    [Fact]
    public async Task AcknowledgeAsync_AlreadyAcknowledged_Idempotent()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var when = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5), a =>
        {
            a.AcknowledgedAt = when;
            a.AcknowledgedById = "firstLead";
            a.AcknowledgedByName = "First";
        });

        await svc.AcknowledgeAsync(abs.Id, Leader("secondLead", "Second"));

        using var db = ctx.NewContext();
        var row = db.Absences.Single(a => a.Id == abs.Id);
        Assert.Equal(when, row.AcknowledgedAt);
        Assert.Equal("firstLead", row.AcknowledgedById); // unchanged
        Assert.Equal("First", row.AcknowledgedByName);
    }

    [Fact]
    public async Task AcknowledgeAsync_NonLeadership_Throws()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);

        var abs = SeedAbsence(ctx, "a1", Today.AddDays(1), Today.AddDays(5));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.AcknowledgeAsync(abs.Id, Owner("a1")));

        using var db = ctx.NewContext();
        Assert.Null(db.Absences.Single(a => a.Id == abs.Id).AcknowledgedAt);
    }

    // ---------- GetAbsentOnAsync (the agent pickers) ----------

    [Fact]
    public async Task GetAbsentOnAsync_CountsBothBoundsAsAbsent()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today.AddDays(2), today.AddDays(4));

        Assert.True((await svc.GetAbsentOnAsync(today.AddDays(2), Owner("me"))).ContainsKey("a1"));
        Assert.True((await svc.GetAbsentOnAsync(today.AddDays(3), Owner("me"))).ContainsKey("a1"));
        Assert.True((await svc.GetAbsentOnAsync(today.AddDays(4), Owner("me"))).ContainsKey("a1"));
    }

    [Fact]
    public async Task GetAbsentOnAsync_LeavesOutTheDaysOnEitherSide()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today.AddDays(2), today.AddDays(4));

        Assert.Empty(await svc.GetAbsentOnAsync(today.AddDays(1), Owner("me")));
        Assert.Empty(await svc.GetAbsentOnAsync(today.AddDays(5), Owner("me")));
    }

    [Fact]
    public async Task GetAbsentOnAsync_ReportsTheEndDay()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today, today.AddDays(9));

        var absent = await svc.GetAbsentOnAsync(today, Owner("me"));

        Assert.Equal(today.AddDays(9), absent["a1"]);
    }

    [Fact]
    public async Task GetAbsentOnAsync_TakesTheLaterEndWhenTwoSignOffsOverlap()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today, today.AddDays(3));
        SeedAbsence(ctx, "a1", today.AddDays(1), today.AddDays(9));

        var absent = await svc.GetAbsentOnAsync(today.AddDays(2), Owner("me"));

        // the picker names one date, and the honest one is the day the agent is actually back
        Assert.Equal(today.AddDays(9), Assert.Single(absent).Value);
    }

    [Fact]
    public async Task GetAbsentOnAsync_SkipsAnyoneWhoIsNotInThePickerAnyway()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("tl", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
            db.Users.Add(Seed.Agent("gone", status: AgentStatus.Terminated));
            db.SaveChanges();
        }
        SeedAbsence(ctx, "tl", today, today.AddDays(2));
        SeedAbsence(ctx, "partner", today, today.AddDays(2));
        SeedAbsence(ctx, "gone", today, today.AddDays(2));
        SeedAbsence(ctx, "a1", today, today.AddDays(2));

        var absent = await svc.GetAbsentOnAsync(today, Owner("me"));

        // the hint follows AgentSelection: a name that can never be picked needs no note either
        Assert.Equal("a1", Assert.Single(absent).Key);
    }

    [Fact]
    public async Task GetAbsentOnAsync_RefusesAPartner()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today, today.AddDays(2));
        var partner = ClaimsPrincipalBuilder.Agent("p").AsPartner(PartnerAgency.LSPD, PartnerRank.Member).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetAbsentOnAsync(today, partner));
    }

    [Fact]
    public async Task TheRosterQueryStaysBlindToAbsences()
    {
        using var ctx = new SqliteTestContext();
        var (svc, _) = Build(ctx);
        var today = Today;
        SeedAbsence(ctx, "a1", today, today.AddDays(5));

        Assert.True((await svc.GetAbsentOnAsync(today, Owner("me"))).ContainsKey("a1"));

        // the roster every picker is built from knows nothing of sign-offs, which is the layer the
        // "warn, never exclude" promise can be pinned at - the rendering above it has no test at all
        using var db = ctx.NewContext();
        Assert.Contains(db.Users.OnlySelectable().ToList(), u => u.Id == "a1");
    }
}
