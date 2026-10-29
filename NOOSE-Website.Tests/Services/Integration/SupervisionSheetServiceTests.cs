using System.Security.Claims;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Cases;
using NOOSE_Website.Data.Entities.Common;
using NOOSE_Website.Data.Entities.Gamification;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Data.Entities.Personnel;
using NOOSE_Website.Infrastructure.Audit;
using NOOSE_Website.Models.CounterIntel;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Personnel;
using NOOSE_Website.Services;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>The supervision sheet: one agent's figures over a window, each beside the team median, and who sees it.</summary>
public sealed class SupervisionSheetServiceTests
{
    private const string Target = "target";

    private static readonly DateTime Now = DateTime.UtcNow;

    private static ClaimsPrincipal Leader()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.Director).Build();

    private static ClaimsPrincipal Supervision()
        => ClaimsPrincipalBuilder.Agent("aufsicht").WithRank(Rank.JuniorAgent).AsTeamLead().Build();

    private static SupervisionSheetService Build(SqliteTestContext ctx, ICounterIntelService? counterIntel = null)
    {
        var rules = Substitute.For<ICounterIntelRuleService>();
        rules.GetActiveAsync(default).ReturnsForAnyArgs(Array.Empty<CounterIntelRuleView>());
        return new SupervisionSheetService(ctx.Factory, new GamificationService(ctx.Factory),
            counterIntel ?? new CounterIntelService(ctx.Factory, rules));
    }

    /// <summary>Three selectable agents for the median, a departed one outside it, and the leadership.</summary>
    private static async Task<SqliteTestContext> RosterAsync(Action<NOOSE_Website.Data.AppDbContext>? seed = null)
    {
        var ctx = new SqliteTestContext();
        await using var db = ctx.NewContext();
        db.Users.Add(Seed.Agent("lead", Rank.Director));
        db.Users.Add(Seed.Agent(Target, Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("b", Rank.SpecialAgent));
        db.Users.Add(Seed.Agent("gone", Rank.SpecialAgent, AgentStatus.Terminated));
        db.Users.Add(Seed.Agent("bewerber", Rank.JuniorAgent, AgentStatus.Applicant));
        seed?.Invoke(db);
        await db.SaveChangesAsync();
        return ctx;
    }

    private static AuditLog Write(string agentId, int daysAgo)
        => new()
        {
            AgentId = agentId, Timestamp = Now.AddDays(-daysAgo), EntityType = nameof(Case), EntityId = "c",
            Action = AuditAction.Modified,
        };

    [Fact]
    public async Task Writes_CountTheWindowBesideTheMedianOfSelectableAgents()
    {
        using var ctx = await RosterAsync(db =>
        {
            db.AuditLogs.AddRange(Write(Target, 1), Write(Target, 2), Write(Target, 3), Write(Target, 20));
            db.AuditLogs.Add(Write(Target, 45));
            db.AuditLogs.AddRange(Write("b", 1), Write("b", 2));
            // the departed agent's work is history: it counts for nobody's median
            for (var i = 0; i < 10; i++)
            {
                db.AuditLogs.Add(Write("gone", 1));
            }
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(4, sheet.Writes.Value);
        // lead 0, target 4, b 2
        Assert.Equal(2, sheet.Writes.Team);
    }

    [Fact]
    public async Task Followups_SplitIntoOnTimeLateAndOverdue()
    {
        using var ctx = await RosterAsync(db =>
        {
            Followup Due(string id, int dueDaysAgo, int? doneDaysAgo, string responsible = Target) => new()
            {
                Id = id, EntityType = nameof(Case), EntityId = "c", ResponsibleAgentId = responsible,
                DueAt = Now.AddDays(-dueDaysAgo), Done = doneDaysAgo is not null,
                DoneAt = doneDaysAgo is { } d ? Now.AddDays(-d) : null,
            };
            db.Followups.AddRange(
                Due("on-time", 5, 6),
                Due("late", 10, 8),
                Due("overdue", 3, null),
                Due("outside", 40, null),
                Due("ahead", -1, null),
                Due("today", 0, null),
                Due("theirs", 3, null, "b"));
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(1, sheet.FollowupsOnTime.Value);
        Assert.Equal(1, sheet.FollowupsLate.Value);
        Assert.Equal(1, sheet.FollowupsOverdue.Value);
        // lead 0, target 1, b 1
        Assert.Equal(1, sheet.FollowupsOverdue.Team);
    }

    [Fact]
    public async Task Meetings_CountOnlySettledAttendanceInTheWindow()
    {
        using var ctx = await RosterAsync(db =>
        {
            var serial = 0;
            void Meeting(int daysAgo, bool closed, MeetingAttendanceStatus status)
            {
                var id = $"m{++serial}";
                db.Meetings.Add(new Meeting
                {
                    Id = id, CaseNumber = $"NOOSE-B-2026-{serial:0000}", Title = id, Start = Now.AddDays(-daysAgo),
                    AttendanceClosedAt = closed ? Now.AddDays(-daysAgo).AddHours(2) : null,
                });
                db.MeetingAttendances.Add(new MeetingAttendance { MeetingId = id, AgentId = Target, Status = status });
            }
            Meeting(5, true, MeetingAttendanceStatus.Present);
            Meeting(10, true, MeetingAttendanceStatus.Missing);
            Meeting(12, true, MeetingAttendanceStatus.SignedOff);
            Meeting(3, false, MeetingAttendanceStatus.Missing);
            Meeting(60, true, MeetingAttendanceStatus.Missing);
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(1, sheet.MeetingsPresent.Value);
        Assert.Equal(1, sheet.MeetingsSignedOff.Value);
        Assert.Equal(1, sheet.MeetingsMissing.Value);
    }

    [Fact]
    public async Task AbsenceDays_CountEachCoveredDayInsideTheWindowOnce()
    {
        var today = MeetingTime.Day(Now);
        using var ctx = await RosterAsync(db =>
        {
            Absence Span(int fromDaysAgo, int toDaysAgo) => new()
            {
                AgentId = Target, FromDate = today.AddDays(-fromDaysAgo), ToDate = today.AddDays(-toDaysAgo),
            };
            // reaches back before the window: only the days from its first day count
            db.Absences.Add(Span(40, 25));
            // two overlapping notices cover days 10 to 6 ago once
            db.Absences.AddRange(Span(10, 7), Span(8, 6));
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        var windowStart = MeetingTime.Day(sheet.SinceUtc);
        var expected = (today.AddDays(-25).DayNumber - windowStart.DayNumber + 1) + 5;
        Assert.Equal(expected, sheet.AbsenceDays.Value);
    }

    [Fact]
    public async Task Points_FollowTheWindowLikeTheLeaderboard()
    {
        using var ctx = await RosterAsync(db =>
        {
            db.People.Add(Seed.Person(configure: p => { p.CreatedById = Target; p.CreatedAt = Now.AddDays(-5); }));
            db.People.Add(Seed.Person(configure: p => { p.CreatedById = Target; p.CreatedAt = Now.AddDays(-50); }));
            db.AgentBadges.Add(new AgentBadge { AgentId = Target, BadgeKey = "x", AwardedAt = Now.AddDays(-200) });
        });
        var svc = Build(ctx);

        var month = await svc.GetAsync(Target, 30, Leader());
        var quarter = await svc.GetAsync(Target, 90, Leader());

        Assert.Equal(3, month!.Points.Value);
        Assert.Equal(6, quarter!.Points.Value);
        // badges are milestones: counted whole, not per window
        Assert.Equal(1, month.Badges.Value);
    }

    [Fact]
    public async Task Notes_CountCommendationsAndDisciplinaryInTheWindow()
    {
        using var ctx = await RosterAsync(db =>
        {
            AgentNote Note(AgentNoteKind kind, int daysAgo) => new()
            {
                AgentId = Target, Kind = kind, EntryDate = Now.AddDays(-daysAgo), Text = "<p>x</p>",
            };
            db.AgentNotes.AddRange(
                Note(AgentNoteKind.Commendation, 3),
                Note(AgentNoteKind.Commendation, 12),
                Note(AgentNoteKind.Disciplinary, 4),
                Note(AgentNoteKind.Disciplinary, 70),
                Note(AgentNoteKind.Information, 2));
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(2, sheet.Commendations.Value);
        Assert.Equal(1, sheet.Disciplinary.Value);
    }

    [Fact]
    public async Task Activity_ReadsTheLatestTraceLikeTheRadar()
    {
        using var ctx = await RosterAsync(db =>
        {
            db.AuditLogs.Add(Write(Target, 5));
            db.AccessLogs.Add(new AccessLog
            {
                AgentId = Target, Timestamp = Now.AddDays(-4), EntityType = nameof(Case), EntityId = "c",
            });
        });
        await using (var db = ctx.NewContext())
        {
            // the visit stamp is written by ExecuteUpdate only; a tracked save ignores it
            await db.Users.Where(a => a.Id == Target)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.LastSeenAt, Now.AddDays(-2)));
        }

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(InactivityTraceKind.Seen, sheet.Activity.Kind);
        Assert.Equal(2, sheet.Activity.SilentDays);
        Assert.Equal(0, sheet.Activity.AbsentDays);
    }

    [Fact]
    public async Task CounterIntel_ShowsTheAccessProfileToLeadership()
    {
        using var ctx = await RosterAsync(db =>
        {
            AccessLog Read(string agentId, string entityId, int daysAgo) => new()
            {
                AgentId = agentId, AgentName = agentId, Timestamp = Now.AddDays(-daysAgo),
                EntityType = nameof(Case), EntityId = entityId,
            };
            db.AccessLogs.AddRange(Read(Target, "c1", 1), Read(Target, "c1", 2), Read(Target, "c2", 3));
            db.AccessLogs.Add(Read(Target, "c3", 50));
            db.AccessLogs.Add(Read("b", "c1", 1));
        });

        var sheet = await Build(ctx).GetAsync(Target, 30, Leader());

        Assert.NotNull(sheet?.CounterIntel);
        Assert.Equal(3, sheet.CounterIntel.Accesses.Value);
        Assert.Equal(2, sheet.CounterIntel.DistinctRecords);
        // lead 0, target 3, b 1
        Assert.Equal(1, sheet.CounterIntel.Accesses.Team);
        Assert.Equal(nameof(Case), Assert.Single(sheet.CounterIntel.ByType).EntityType);
    }

    [Fact]
    public async Task CounterIntel_CountsTheAgentsOwnRowsWhenTheLogIsBusy()
    {
        // the cockpit reads the newest 30,000 rows of everyone; a year of a busy office must not crowd the agent out
        using var ctx = await RosterAsync(db =>
        {
            db.AccessLogs.Add(new AccessLog
            {
                AgentId = Target, Timestamp = Now.AddDays(-200), EntityType = nameof(Case), EntityId = "c1",
            });
            db.ChangeTracker.AutoDetectChangesEnabled = false;
            for (var i = 0; i < 30_001; i++)
            {
                db.AccessLogs.Add(new AccessLog
                {
                    AgentId = "b", Timestamp = Now.AddMinutes(-i), EntityType = nameof(Case), EntityId = "c2",
                });
            }
        });

        var sheet = await Build(ctx).GetAsync(Target, 365, Leader());

        Assert.Equal(1, sheet!.CounterIntel!.Accesses.Value);
    }

    [Fact]
    public async Task CounterIntel_ListsOnlyThisAgentsFlags()
    {
        using var ctx = await RosterAsync();
        var counterIntel = Substitute.For<ICounterIntelService>();
        counterIntel.GetFlagsAsync(default!).ReturnsForAnyArgs(new[]
        {
            new InsiderFlag(Target, "Codename-target", "Nachtzugriffe", "12 Zugriffe", 12, null),
            new InsiderFlag("b", "Codename-b", "Nachtzugriffe", "3 Zugriffe", 3, null),
        });

        var sheet = await Build(ctx, counterIntel).GetAsync(Target, 30, Leader());

        Assert.Equal(Target, Assert.Single(sheet!.CounterIntel!.Flags).AgentId);
    }

    [Fact]
    public async Task TheSupervision_SeesTheSheetWithoutCounterIntel()
    {
        using var ctx = await RosterAsync(db => db.AuditLogs.Add(Write(Target, 1)));
        var counterIntel = Substitute.For<ICounterIntelService>();

        var sheet = await Build(ctx, counterIntel).GetAsync(Target, 30, Supervision());

        Assert.NotNull(sheet);
        Assert.Equal(1, sheet.Writes.Value);
        Assert.Null(sheet.CounterIntel);
        // the cockpit's own guard would refuse; the sheet must not even ask
        await counterIntel.DidNotReceiveWithAnyArgs().GetFlagsAsync(default!);
        await counterIntel.DidNotReceiveWithAnyArgs().GetAgentProfileAsync(default!, default!);
    }

    public static TheoryData<string> Refused => new() { "senior", "partner", "hrb" };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task OutsideTheLeadership_TheSheetIsRefused(string who)
    {
        using var ctx = await RosterAsync();
        ClaimsPrincipal viewer = who switch
        {
            "senior" => ClaimsPrincipalBuilder.Agent("senior").WithRank(Rank.SeniorSpecialAgent).Build(),
            "partner" => ClaimsPrincipalBuilder.Agent("partner").WithRank(Rank.Director)
                .AsPartner(PartnerAgency.LSPD, PartnerRank.Chief).Build(),
            _ => ClaimsPrincipalBuilder.Agent("hrb").WithRank(Rank.SpecialAgent).AsHrb().Build(),
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Build(ctx).GetAsync(Target, 30, viewer));
    }

    [Theory]
    [InlineData("bewerber")]
    [InlineData("nobody")]
    public async Task ApplicantsAndUnknownIds_HaveNoSheet(string agentId)
    {
        using var ctx = await RosterAsync();

        Assert.Null(await Build(ctx).GetAsync(agentId, 30, Leader()));
    }

    [Fact]
    public async Task ADepartedAgent_StillGetsTheirSheet()
    {
        using var ctx = await RosterAsync(db => db.AuditLogs.Add(Write("gone", 3)));

        var sheet = await Build(ctx).GetAsync("gone", 30, Leader());

        Assert.NotNull(sheet);
        Assert.Equal(1, sheet.Writes.Value);
    }

    [Theory]
    [InlineData(30, 30)]
    [InlineData(365, 365)]
    [InlineData(7, 90)]
    [InlineData(0, 90)]
    public async Task TheWindow_IsOneOfTheOfferedChoices(int asked, int used)
    {
        using var ctx = await RosterAsync();

        var sheet = await Build(ctx).GetAsync(Target, asked, Leader());

        Assert.Equal(used, sheet!.Days);
    }
}
