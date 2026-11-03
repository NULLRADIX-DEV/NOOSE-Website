using System.Security.Claims;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.People;
using NOOSE_Website.Data.Entities.Public;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Infrastructure.CurrentUser;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Public;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Guard tests for <see cref="BuergerService"/>: who may act, and what a block actually blocks.</summary>
public sealed class BuergerServiceTests
{
    private static ClaimsPrincipal Citizen(string id = "buerger-1")
        => ClaimsPrincipalBuilder.Agent(id).WithStatus(AgentStatus.Civilian).Build();

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).WithCodename("Falcon").Build();

    private static ClaimsPrincipal PlainAgent()
        => ClaimsPrincipalBuilder.Agent("agent-1").WithRank(Rank.SpecialAgent).Build();

    /// <summary>Read-only supervision: leadership rank but no admin flag plus the team-lead marker.</summary>
    private static ClaimsPrincipal OnlyReader()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.Director).AsTeamLead().Build();

    private static ClaimsPrincipal Partner()
        => ClaimsPrincipalBuilder.Agent("partner").AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build();

    private static async Task<SqliteTestContext> SeededAsync()
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("buerger-1", status: AgentStatus.Civilian,
            configure: a => { a.Codename = string.Empty; a.DiscordUsername = "spieler_max"; }));
        db.Users.Add(Seed.Agent("buerger-2", status: AgentStatus.Civilian,
            configure: a => { a.Codename = string.Empty; a.DiscordUsername = "spieler_lena"; }));
        db.Users.Add(Seed.Agent("lead", rank: Rank.Director));
        // accounts that may also hold a civilian identity
        db.Users.Add(Seed.Agent("agent-1", rank: Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("bew", status: AgentStatus.Applicant));
        db.Users.Add(Seed.Agent("partner", configure: a => a.PartnerAgency = PartnerAgency.LSPD));
        db.Users.Add(Seed.Agent("aufsicht", rank: Rank.Director, configure: a => a.IsTeamLead = true));
        await db.SaveChangesAsync();
        return ctx;
    }

    private static BuergerService NewService(SqliteTestContext ctx) => new(ctx.Factory);

    // ---- the name is settled at the first save ----

    [Fact]
    public async Task TheNameCannotBeChangedBySelfOnceItIsSet()
    {
        // it is an identity claim elsewhere — the objection gate compares it against a published notice — so a
        // freely rewritable name would have made that gate self-asserted
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveOwnAsync("Anna", "Andere", Citizen()));

        Assert.Equal(BuergerNameRules.LockedMessage, error.Message);
        await using var db = ctx.NewContext();
        var profile = await db.BuergerProfile.SingleAsync(p => p.UserId == "buerger-1");
        Assert.Equal("Max", profile.FirstName);
        Assert.Equal("Mustermann", profile.LastName);
    }

    [Fact]
    public async Task SavingTheSameNameAgainIsNotAChange()
    {
        // a double submit, a re-render, a back button: none of them is an attempt to rename
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());

        await service.SaveOwnAsync("  Max  ", "Mustermann", Citizen());

        await using var db = ctx.NewContext();
        Assert.Equal("Max", (await db.BuergerProfile.SingleAsync(p => p.UserId == "buerger-1")).FirstName);
    }

    [Fact]
    public async Task AHalfFilledProfileIsNotLockedYet()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            db.BuergerProfile.Add(new BuergerProfil { UserId = "buerger-1", FirstName = "Max", LastName = "" });
            await db.SaveChangesAsync();
        }
        var service = NewService(ctx);

        await service.SaveOwnAsync("Moritz", "Mustermann", Citizen());

        await using var check = ctx.NewContext();
        var profile = await check.BuergerProfile.SingleAsync(p => p.UserId == "buerger-1");
        Assert.Equal("Moritz", profile.FirstName);
        Assert.Equal("Mustermann", profile.LastName);
    }

    [Fact]
    public async Task LeadershipMayCorrectASettledName()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Xx", "Trollname", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;

        await service.SetNameAsync(id, " Max ", " Mustermann ", Leader());

        await using var db = ctx.NewContext();
        var profile = await db.BuergerProfile.SingleAsync(p => p.Id == id);
        Assert.Equal("Max", profile.FirstName);
        Assert.Equal("Mustermann", profile.LastName);
    }

    [Fact]
    public async Task APlainAgentMayNotCorrectAName()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SetNameAsync(id, "Anna", "Andere", PlainAgent()));
    }

    [Fact]
    public async Task TheReadOnlySupervisionMayNotCorrectAName()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SetNameAsync(id, "Anna", "Andere", OnlyReader()));
    }

    // ---- shutting an account out of the site ----

    [Fact]
    public async Task BlockingTheAccountEndsEveryAccessNotJustSubmissions()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Xx", "Trollname", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;

        await service.BlockAccountAsync(id, "Troll-Name, kein IC-Bezug", Leader());

        await using var db = ctx.NewContext();
        var user = await db.Users.SingleAsync(u => u.Id == "buerger-1");
        // Blocked fails AgentStatusRules.MayHoldSession, so the login refuses and a running circuit is evicted
        Assert.Equal(AgentStatus.Blocked, user.Status);
        Assert.False(AgentStatusRules.MayHoldSession(user.Status));
        // and the submission block is set too, so the two levels never disagree
        Assert.True((await db.BuergerProfile.SingleAsync(p => p.Id == id)).IsBlocked);
    }

    [Fact]
    public async Task BlockingTheAccountRotatesTheSecurityStamp()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Xx", "Trollname", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;
        string? before;
        await using (var db = ctx.NewContext())
        {
            before = (await db.Users.SingleAsync(u => u.Id == "buerger-1")).SecurityStamp;
        }

        await service.BlockAccountAsync(id, "Troll-Name", Leader());

        await using var check = ctx.NewContext();
        Assert.NotEqual(before, (await check.Users.SingleAsync(u => u.Id == "buerger-1")).SecurityStamp);
    }

    [Fact]
    public async Task LiftingTheLockoutRestoresACitizenNeverAnAgent()
    {
        // the agent unblock path sets Active; using it here would hand a citizen the whole records database
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;
        await service.BlockAccountAsync(id, "Versehen", Leader());

        await service.UnblockAccountAsync(id, Leader());

        await using var db = ctx.NewContext();
        Assert.Equal(AgentStatus.Civilian, (await db.Users.SingleAsync(u => u.Id == "buerger-1")).Status);
        Assert.False((await db.BuergerProfile.SingleAsync(p => p.Id == id)).IsBlocked);
    }

    [Fact]
    public async Task AnAgentAccountIsNotShutOutThroughTheCitizenDesk()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen("agent-1"));
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "agent-1").Id;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BlockAccountAsync(id, "aus Versehen", Leader()));

        await using var db = ctx.NewContext();
        Assert.Equal(AgentStatus.Active, (await db.Users.SingleAsync(u => u.Id == "agent-1")).Status);
    }

    [Fact]
    public async Task NobodyShutsTheirOwnCivilIdentityOut()
    {
        // leadership holds a civilian identity too; locking yourself out of your own account is never the intent
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            (await db.Users.SingleAsync(u => u.Id == "lead")).Status = AgentStatus.Civilian;
            await db.SaveChangesAsync();
        }
        var service = NewService(ctx);
        await service.SaveOwnAsync("Falco", "Falkner", Citizen("lead"));
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "lead").Id;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BlockAccountAsync(id, "Selbsttest", Leader()));
    }

    [Fact]
    public async Task AnAccountLockoutNeedsAReason()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.BlockAccountAsync(id, "   ", Leader()));
    }

    [Fact]
    public async Task TheRosterShowsWhichAccountsAreShutOut()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;
        await service.BlockAccountAsync(id, "Troll-Name", Leader());

        var row = (await service.ListAsync(Leader())).Single(r => r.Id == id);

        Assert.True(row.AccountBlocked);
        Assert.True(row.NameLocked);
    }

    // ---- own profile ----

    [Fact]
    public async Task SaveOwnAsync_CreatesTrimmedProfile()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await service.SaveOwnAsync("  Max ", " Mustermann  ", Citizen());

        await using var db = ctx.NewContext();
        var profile = await db.BuergerProfile.SingleAsync(p => p.UserId == "buerger-1");
        Assert.Equal("Max", profile.FirstName);
        Assert.Equal("Mustermann", profile.LastName);
        Assert.False(profile.IsBlocked);
    }

    [Fact]
    public async Task SaveOwnAsync_SecondCall_TouchesTheOneRowAndNeverAddsASecond()
    {
        // the name itself is settled at the first save now; what this still pins is the read-then-insert path,
        // which must find its own row rather than write a second profile for the same account
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());

        await using var db = ctx.NewContext();
        var profiles = await db.BuergerProfile.Where(p => p.UserId == "buerger-1").ToListAsync();
        Assert.Single(profiles);
        Assert.Equal("Max", profiles[0].FirstName);
    }

    [Fact]
    public async Task SaveOwnAsync_AllowedForAgentsAndApplicants()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var applicant = ClaimsPrincipalBuilder.Agent("bew").WithStatus(AgentStatus.Applicant).Build();

        // the civilian identity is a second, separate identity — an agent or applicant may hold one too
        await service.SaveOwnAsync("Max", "Mustermann", PlainAgent());
        await service.SaveOwnAsync("Lena", "Schmitt", applicant);

        await using var db = ctx.NewContext();
        Assert.Equal("Mustermann", (await db.BuergerProfile.SingleAsync(p => p.UserId == "agent-1")).LastName);
        Assert.Equal("Schmitt", (await db.BuergerProfile.SingleAsync(p => p.UserId == "bew")).LastName);
    }

    [Fact]
    public async Task SaveOwnAsync_AllowedForPartnersAndTheReadOnlySupervision()
    {
        // both file tickets and tips the way a citizen does, and the desk needs a name behind them — the civilian
        // identity is the one write these two accounts get anywhere
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await service.SaveOwnAsync("Trevor", "Ward", Partner());
        await service.SaveOwnAsync("Nora", "Kessler", OnlyReader());

        await using var db = ctx.NewContext();
        Assert.Equal("Ward", (await db.BuergerProfile.SingleAsync(p => p.UserId == "partner")).LastName);
        Assert.Equal("Kessler", (await db.BuergerProfile.SingleAsync(p => p.UserId == "aufsicht")).LastName);
    }

    [Fact]
    public async Task SaveOwnAsync_DeniedForDemoAndAnonymous()
    {
        // the demo principal is shared by every anonymous guest; an identity of its own would be everyone's
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var demo = ClaimsPrincipalBuilder.Agent("demo").AsDemo().Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SaveOwnAsync("Max", "Mustermann", demo));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.SaveOwnAsync("Max", "Mustermann", ClaimsPrincipalBuilder.Anonymous()));
    }

    [Fact]
    public async Task GetOwnAsync_ReadableForEverySignedInAccount_ButNotAnonymous()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        Assert.Null(await service.GetOwnAsync(PlainAgent()));
        Assert.Null(await service.GetOwnAsync(Leader()));
        Assert.Null(await service.GetOwnAsync(OnlyReader()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.GetOwnAsync(ClaimsPrincipalBuilder.Anonymous()));
    }

    [Theory]
    [InlineData("", "Mustermann")]
    [InlineData("   ", "Mustermann")]
    [InlineData("Max", "")]
    public async Task SaveOwnAsync_RejectsBlankNames(string first, string last)
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SaveOwnAsync(first, last, Citizen()));
    }

    [Fact]
    public async Task SaveOwnAsync_RejectsOverlongName()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveOwnAsync(new string('A', 65), "Mustermann", Citizen()));
    }

    [Fact]
    public async Task ABlockedCitizenIsNoMoreAbleToRenameThemselvesThanAnyOther()
    {
        // the block governs submissions and the lock governs identity; neither hands the other an exception
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = await IdOfAsync(ctx, "buerger-1");
        await service.BlockAsync(id, "Spam", Leader());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.SaveOwnAsync("Maximilian", "Mustermann", Citizen()));

        await using var db = ctx.NewContext();
        var profile = await db.BuergerProfile.SingleAsync(p => p.UserId == "buerger-1");
        Assert.Equal("Max", profile.FirstName);
        Assert.True(profile.IsBlocked);
    }

    [Fact]
    public async Task HasCompleteProfileAsync_FalseBeforeSave_TrueAfter()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        Assert.False(await service.HasCompleteProfileAsync(Citizen()));
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        Assert.True(await service.HasCompleteProfileAsync(Citizen()));
    }

    [Fact]
    public async Task GetOwnAsync_NeverReturnsAnotherCitizensProfile()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen("buerger-1"));

        Assert.Null(await service.GetOwnAsync(Citizen("buerger-2")));
    }

    // ---- submission guard ----

    [Fact]
    public async Task RequireSubmittingCitizenAsync_ThrowsWithoutProfile()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RequireSubmittingCitizenAsync(Citizen()));
    }

    [Fact]
    public async Task RequireSubmittingCitizenAsync_ThrowsWhenIncomplete()
    {
        using var ctx = await SeededAsync();
        await using (var db = ctx.NewContext())
        {
            // a hand-written half profile: the guard must not trust that SaveOwnAsync was the only writer
            db.BuergerProfile.Add(new BuergerProfil { UserId = "buerger-1", FirstName = "Max", LastName = string.Empty });
            await db.SaveChangesAsync();
        }
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.RequireSubmittingCitizenAsync(Citizen()));
    }

    [Fact]
    public async Task RequireSubmittingCitizenAsync_ThrowsWhenBlocked()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        await service.BlockAsync(await IdOfAsync(ctx, "buerger-1"), "Spam", Leader());

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.RequireSubmittingCitizenAsync(Citizen()));
    }

    [Fact]
    public async Task RequireSubmittingCitizenAsync_ReturnsProfileWhenComplete()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());

        var profile = await service.RequireSubmittingCitizenAsync(Citizen());

        Assert.Equal("Mustermann", profile.LastName);
    }

    // ---- roster and blocking ----

    [Fact]
    public async Task ListAsync_RequiresLeadership()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(PlainAgent()));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(Citizen()));
    }

    [Fact]
    public async Task ListAsync_CarriesDiscordHandleAndFiltersBySearch()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen("buerger-1"));
        await service.SaveOwnAsync("Lena", "Schmitt", Citizen("buerger-2"));

        var all = await service.ListAsync(Leader());
        Assert.Equal(2, all.Count);
        Assert.Contains(all, r => r.DiscordUsername == "spieler_max" && r.DisplayName == "Max Mustermann");

        Assert.Single(await service.ListAsync(Leader(), "Schmitt"));
        Assert.Single(await service.ListAsync(Leader(), "spieler_max"));
        Assert.Empty(await service.ListAsync(Leader(), "Niemand"));
    }

    [Fact]
    public async Task BlockAsync_RequiresAReason()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = await IdOfAsync(ctx, "buerger-1");

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BlockAsync(id, "   ", Leader()));
    }

    [Fact]
    public async Task BlockAsync_DeniedForOnlyReaderAndPlainAgent()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = await IdOfAsync(ctx, "buerger-1");

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BlockAsync(id, "Spam", PlainAgent()));
        // read-only supervision passes the rank gate but must never write
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BlockAsync(id, "Spam", OnlyReader()));
    }

    [Fact]
    public async Task UnblockAsync_ClearsTheFlagButKeepsTheReason()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        var id = await IdOfAsync(ctx, "buerger-1");
        await service.BlockAsync(id, "Spam", Leader());

        await service.UnblockAsync(id, Leader());

        await using var db = ctx.NewContext();
        var profile = await db.BuergerProfile.SingleAsync(p => p.Id == id);
        Assert.False(profile.IsBlocked);
        Assert.Equal("Spam", profile.BlockedReason);
        Assert.Equal("lead", profile.BlockedById);
    }

    [Fact]
    public async Task BlockAsync_UnknownProfile_Throws()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.BlockAsync("nope", "Spam", Leader()));
    }

    // ---- audit ----

    /// <summary>Stub acting agent for the interceptor-backed audit test.</summary>
    private sealed class FixedUser : ICurrentUserService
    {
        public Task<CurrentUserInfo> GetAsync() => Task.FromResult(Get());

        public CurrentUserInfo Get() => new("buerger-1", null, false, false, false);
    }

    [Fact]
    public async Task NameChange_IsAuditedByTheInterceptor_WithoutAManualRow()
    {
        using var ctx = new SqliteTestContext();
        // the shared context omits the interceptors on purpose; BuergerProfil is IAuditable, so wiring
        // the real one up is what proves a rename is logged without ManualAudit anywhere in the service
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite(ctx.Connection)
            .AddInterceptors(new AuditSaveChangesInterceptor(new FixedUser()))
            .Options;
        await using (var db = new AppDbContext(options))
        {
            db.Users.Add(Seed.Agent("buerger-1", status: AgentStatus.Civilian,
                configure: a => a.Codename = string.Empty));
            await db.SaveChangesAsync();
        }
        var service = new BuergerService(new TestDbContextFactory(options));

        await service.SaveOwnAsync("Max", "Mustermann", Citizen());
        // the citizen cannot rename themselves any more, so the audited change is the leadership correction
        var id = (await service.ListAsync(Leader())).Single(r => r.UserId == "buerger-1").Id;
        await service.SetNameAsync(id, "Maximilian", "Mustermann", Leader());

        await using var read = ctx.NewContext();
        var rows = await read.AuditLogs
            .Where(a => a.EntityType == nameof(BuergerProfil))
            .OrderBy(a => a.Id)
            .ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal(AuditAction.Created, rows[0].Action);
        Assert.Equal(AuditAction.Modified, rows[1].Action);
        Assert.Contains("Max", rows[1].ChangesJson);
    }

    private static async Task<string> IdOfAsync(SqliteTestContext ctx, string userId)
    {
        await using var db = ctx.NewContext();
        return await db.BuergerProfile.Where(p => p.UserId == userId).Select(p => p.Id).SingleAsync();
    }
    // ---- linked person file ----

    private static async Task<string> ProfileAsync(SqliteTestContext ctx, BuergerService service)
    {
        var profile = await service.SaveOwnAsync("Erika", "Musterfrau", Citizen());
        return profile.Id;
    }

    private static async Task SeedPeopleAsync(SqliteTestContext ctx)
    {
        await using var db = ctx.NewContext();
        db.People.Add(Seed.Person("p1", "Max Mustermann", p => p.CaseNumber = "FIB-P-2026-0001"));
        db.People.Add(Seed.Person("p2", "Verschluss", p =>
        {
            p.CaseNumber = "FIB-P-2026-0002";
            p.IsClassified = true;
        }));
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Leadership_ties_a_citizen_account_to_a_person_file()
    {
        using var ctx = await SeededAsync();
        await SeedPeopleAsync(ctx);
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);

        await service.LinkPersonAsync(profileId, "p1", Leader());

        var linked = await service.GetLinkedPersonAsync(profileId, Leader());
        Assert.NotNull(linked);
        Assert.Equal("p1", linked!.PersonId);
        Assert.Equal("FIB-P-2026-0001", linked.CaseNumber);
    }

    [Fact]
    public async Task Untying_clears_the_link()
    {
        using var ctx = await SeededAsync();
        await SeedPeopleAsync(ctx);
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);
        await service.LinkPersonAsync(profileId, "p1", Leader());

        await service.LinkPersonAsync(profileId, null, Leader());

        Assert.Null(await service.GetLinkedPersonAsync(profileId, Leader()));
    }

    [Fact]
    public async Task A_plain_agent_may_not_tie_an_account_to_a_file()
    {
        using var ctx = await SeededAsync();
        await SeedPeopleAsync(ctx);
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.LinkPersonAsync(profileId, "p1", PlainAgent()));
    }

    [Fact]
    public async Task Read_only_supervision_may_not_tie_an_account_to_a_file()
    {
        using var ctx = await SeededAsync();
        await SeedPeopleAsync(ctx);
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => service.LinkPersonAsync(profileId, "p1", OnlyReader()));
    }

    [Fact]
    public async Task An_unknown_person_file_is_refused()
    {
        using var ctx = await SeededAsync();
        await SeedPeopleAsync(ctx);
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.LinkPersonAsync(profileId, "gibt-es-nicht", Leader()));
    }

    [Fact]
    public async Task The_trust_counter_is_recomputed_from_the_tips_themselves()
    {
        using var ctx = await SeededAsync();
        var service = NewService(ctx);
        var profileId = await ProfileAsync(ctx, service);
        await using (var db = ctx.NewContext())
        {
            db.Hinweise.Add(new Hinweis
            {
                CaseNumber = "FIB-H-2026-0001", CitizenProfileId = profileId,
                Text = "Bestätigt", Status = TipStatus.Bestaetigt,
            });
            db.Hinweise.Add(new Hinweis
            {
                CaseNumber = "FIB-H-2026-0002", CitizenProfileId = profileId,
                Text = "Führte zur Ergreifung", Status = TipStatus.FuehrteZurErgreifung,
            });
            db.Hinweise.Add(new Hinweis
            {
                CaseNumber = "FIB-H-2026-0003", CitizenProfileId = profileId,
                Text = "Offen", Status = TipStatus.InPruefung,
            });
            await db.SaveChangesAsync();
        }

        await service.RecomputeConfirmedTipsAsync(profileId);

        await using var check = ctx.NewContext();
        Assert.Equal(2, await check.BuergerProfile.Where(p => p.Id == profileId)
            .Select(p => p.ConfirmedTips).SingleAsync());
    }
}
