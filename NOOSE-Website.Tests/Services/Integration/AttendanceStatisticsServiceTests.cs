using System.Linq;
using System.Security.Claims;
using NOOSE_Website.Data.Entities.Absences;
using NOOSE_Website.Data.Entities.Meetings;
using NOOSE_Website.Models.Common;
using NOOSE_Website.Models.Dashboard;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Statistics;
using NOOSE_Website.Services;
using NOOSE_Website.Services.Statistics;
using NSubstitute;

namespace NOOSE_Website.Tests.Services.Integration;

/// <summary>Integration tests for <see cref="AttendanceStatisticsService"/> over in-memory SQLite.</summary>
public sealed class AttendanceStatisticsServiceTests
{
    private static readonly DateTime Stamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static AttendanceStatisticsService Build(SqliteTestContext ctx, int window, int yellow, int red)
    {
        var settings = Substitute.For<ISystemSettingService>();
        settings.GetAsync(Arg.Any<CancellationToken>()).Returns(Config(window, yellow, red));
        return new AttendanceStatisticsService(ctx.Factory, settings);
    }

    private static SystemConfiguration Config(int window, int yellow, int red) => new(
        MaintenanceModeActive: false,
        MaintenanceModeText: null,
        BannerText: null,
        BannerLevel: BannerLevels.Info,
        ThemePrimary: null,
        ThemeSecondary: null,
        ThemeTertiary: null,
        LogoFileName: null,
        LogoContentType: null,
        DemoModeActive: false,
        WantedBoardMinHazard: HazardLevel.Critical,
        MeetingWindowSize: window,
        MeetingAnomalyYellow: yellow,
        MeetingAnomalyRed: red);

    /// <summary>Passes RequireClassifiedRead via minimum leadership rank.</summary>
    private static ClaimsPrincipal Lead()
        => ClaimsPrincipalBuilder.Agent("lead").WithRank(Rank.SectionChief).Build();

    private static Meeting Meeting(string id, DateTime startUtc, bool closed) => new()
    {
        Id = id,
        Title = id,
        CaseNumber = $"FIB-BS-2026-{id}",
        Start = startUtc,
        AttendanceClosedAt = closed ? startUtc : null,
        CreatedAt = startUtc,
    };

    private static MeetingAttendance Att(string meetingId, string agentId, MeetingAttendanceStatus status) => new()
    {
        MeetingId = meetingId,
        AgentId = agentId,
        Status = status,
        CreatedAt = Stamp,
    };

    private static Absence Abs(string agentId, DateOnly from, AbsenceCategory category, bool acknowledged) => new()
    {
        AgentId = agentId,
        FromDate = from,
        ToDate = from,
        Days = 1,
        Category = category,
        AcknowledgedAt = acknowledged ? Stamp : null,
        CreatedAt = Stamp,
    };

    private static int Seg(IReadOnlyList<DistributionSegment> list, string designation)
        => list.Single(s => s.Designation == designation).Count;

    // ---------- authorization gate ----------

    [Fact]
    public async Task GetReportAsync_ActorFailsClassifiedRead_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        // SpecialAgent: below leadership, not admin, not team lead -> MayClassifiedRead is false.
        var actor = ClaimsPrincipalBuilder.Agent("plain").WithRank(Rank.SpecialAgent).Build();

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => svc.GetReportAsync(actor));
    }

    [Fact]
    public async Task GetReportAsync_AnonymousActor_Throws()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => svc.GetReportAsync(ClaimsPrincipalBuilder.Anonymous()));
    }

    [Fact]
    public async Task GetReportAsync_OnlyReaderTeamLead_PassesGate()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        // Read-only supervision (team lead, not admin) must be admitted by the page policy.
        var reader = ClaimsPrincipalBuilder.Agent("reader").WithRank(Rank.SpecialAgent).AsTeamLead().Build();

        var report = await svc.GetReportAsync(reader);

        Assert.NotNull(report);
        Assert.Equal(5, report.WindowSize);
    }

    // ---------- empty database ----------

    [Fact]
    public async Task GetReportAsync_EmptyDatabase_ReturnsCoherentEmptyReport()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        var report = await svc.GetReportAsync(Lead());

        Assert.Equal(5, report.WindowSize);
        Assert.Equal(2, report.YellowThreshold);
        Assert.Equal(3, report.RedThreshold);
        Assert.Equal(0, report.MeetingsEvaluated);
        Assert.Equal(0, report.AbsencesOpenAcknowledgement);
        Assert.Empty(report.Anomalies);
        Assert.Empty(report.AllAgents);

        // The three attendance states are always present as zero segments.
        Assert.Equal(3, report.AttendanceDistribution.Count);
        Assert.All(report.AttendanceDistribution, s => Assert.Equal(0, s.Count));

        // One segment per absence category, all zero.
        Assert.Equal(AbsenceCategoryDisplay.All.Count, report.AbsencesByCategory.Count);
        Assert.All(report.AbsencesByCategory, s => Assert.Equal(0, s.Count));

        // Fixed twelve-month rolling series.
        Assert.Equal(12, report.TimeSeries.Count);
        Assert.Equal(12, report.AbsenceCountsPerMonth.Count);
        Assert.Equal(12, report.MissingCountsPerMonth.Count);
        Assert.Equal(0, report.AbsenceCountsPerMonth.Sum());
        Assert.Equal(0, report.MissingCountsPerMonth.Sum());
    }

    // ---------- threshold clamping ----------

    [Fact]
    public async Task GetReportAsync_ClampsIncoherentThresholds()
    {
        using var ctx = new SqliteTestContext();
        // window=0 (< red), yellow=60 (> cap), red=1 (< yellow): all clamp up to the 50 cap.
        var svc = Build(ctx, 0, 60, 1);

        var report = await svc.GetReportAsync(Lead());

        Assert.Equal(50, report.YellowThreshold);
        Assert.Equal(50, report.RedThreshold);
        Assert.Equal(50, report.WindowSize);
    }

    // ---------- per-agent counts, distribution, filtering, anomalies ----------

    [Fact]
    public async Task GetReportAsync_ComputesPerAgentCountsDistributionAndAnomalies()
    {
        using var ctx = new SqliteTestContext();
        // Coherent(3,2,3) -> window=3, yellow=2, red=3.
        var svc = Build(ctx, 3, 2, 3);

        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("m1", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("m2", new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("m3", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("mOpen", new DateTime(2026, 4, 1, 12, 0, 0, DateTimeKind.Utc), closed: false));

            // Active internal agents that appear in the roster.
            db.Users.Add(Seed.Agent("red1", Rank.SpecialAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("yellow1", Rank.SpecialAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("clean1", Rank.SpecialAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("excused1", Rank.SpecialAgent, AgentStatus.Active));

            // Excluded from the roster: team lead, external partner, non-active.
            db.Users.Add(Seed.Agent("tl1", configure: a => a.IsTeamLead = true));
            db.Users.Add(Seed.Agent("partner1", configure: a => a.PartnerAgency = PartnerAgency.DoJ));
            db.Users.Add(Seed.Agent("pending1", status: AgentStatus.Pending));

            // red1: three misses -> Red.
            db.MeetingAttendances.Add(Att("m1", "red1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m2", "red1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m3", "red1", MeetingAttendanceStatus.Missing));

            // yellow1: two misses, one present -> Yellow.
            db.MeetingAttendances.Add(Att("m1", "yellow1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m2", "yellow1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m3", "yellow1", MeetingAttendanceStatus.Present));

            // clean1: three present -> None. Extra miss on the OPEN meeting must be ignored.
            db.MeetingAttendances.Add(Att("m1", "clean1", MeetingAttendanceStatus.Present));
            db.MeetingAttendances.Add(Att("m2", "clean1", MeetingAttendanceStatus.Present));
            db.MeetingAttendances.Add(Att("m3", "clean1", MeetingAttendanceStatus.Present));
            db.MeetingAttendances.Add(Att("mOpen", "clean1", MeetingAttendanceStatus.Missing));

            // excused1: three signed-off -> None.
            db.MeetingAttendances.Add(Att("m1", "excused1", MeetingAttendanceStatus.SignedOff));
            db.MeetingAttendances.Add(Att("m2", "excused1", MeetingAttendanceStatus.SignedOff));
            db.MeetingAttendances.Add(Att("m3", "excused1", MeetingAttendanceStatus.SignedOff));

            db.SaveChanges();
        }

        var report = await svc.GetReportAsync(Lead());

        Assert.Equal(3, report.WindowSize);
        Assert.Equal(3, report.MeetingsEvaluated);

        // Roster excludes team lead, partner, non-active.
        Assert.Equal(4, report.AllAgents.Count);
        Assert.DoesNotContain(report.AllAgents, a => a.AgentId is "tl1" or "partner1" or "pending1");
        // Deterministic order by codename ("Codename-clean1" is first).
        Assert.Equal("clean1", report.AllAgents[0].AgentId);

        var red = report.AllAgents.Single(a => a.AgentId == "red1");
        Assert.Equal(3, red.Evaluated);
        Assert.Equal(0, red.Present);
        Assert.Equal(0, red.Excused);
        Assert.Equal(3, red.Missing);
        Assert.Equal(AttendanceAnomalyLevel.Red, red.Level);
        Assert.Equal("/personal/red1", red.Href);
        Assert.Equal("Codename-red1", red.Codename);

        var yellow = report.AllAgents.Single(a => a.AgentId == "yellow1");
        Assert.Equal(3, yellow.Evaluated);
        Assert.Equal(1, yellow.Present);
        Assert.Equal(2, yellow.Missing);
        Assert.Equal(AttendanceAnomalyLevel.Yellow, yellow.Level);

        var clean = report.AllAgents.Single(a => a.AgentId == "clean1");
        Assert.Equal(3, clean.Evaluated);
        Assert.Equal(3, clean.Present);
        Assert.Equal(0, clean.Missing); // the open-meeting miss did not leak in
        Assert.Equal(AttendanceAnomalyLevel.None, clean.Level);

        var excused = report.AllAgents.Single(a => a.AgentId == "excused1");
        Assert.Equal(3, excused.Excused);
        Assert.Equal(0, excused.Missing);
        Assert.Equal(AttendanceAnomalyLevel.None, excused.Level);

        // Anomalies: only Yellow/Red, most severe first.
        Assert.Equal(2, report.Anomalies.Count);
        Assert.Equal("red1", report.Anomalies[0].AgentId);
        Assert.Equal(AttendanceAnomalyLevel.Red, report.Anomalies[0].Level);
        Assert.Equal("yellow1", report.Anomalies[1].AgentId);

        // Distribution over all in-window rows (12 total): 4 present, 3 signed-off, 5 missing.
        Assert.Equal(4, Seg(report.AttendanceDistribution, MeetingAttendanceStatusDisplay.Name(MeetingAttendanceStatus.Present)));
        Assert.Equal(3, Seg(report.AttendanceDistribution, MeetingAttendanceStatusDisplay.Name(MeetingAttendanceStatus.SignedOff)));
        Assert.Equal(5, Seg(report.AttendanceDistribution, MeetingAttendanceStatusDisplay.Name(MeetingAttendanceStatus.Missing)));
    }

    [Fact]
    public async Task GetReportAsync_TopNTruncatesAnomalyList()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 3, 2, 3);

        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("m1", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("m2", new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("m3", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));

            db.Users.Add(Seed.Agent("red1", Rank.SpecialAgent, AgentStatus.Active));
            db.Users.Add(Seed.Agent("yellow1", Rank.SpecialAgent, AgentStatus.Active));

            foreach (var m in new[] { "m1", "m2", "m3" })
            {
                db.MeetingAttendances.Add(Att(m, "red1", MeetingAttendanceStatus.Missing));
            }
            db.MeetingAttendances.Add(Att("m1", "yellow1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m2", "yellow1", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("m3", "yellow1", MeetingAttendanceStatus.Present));

            db.SaveChanges();
        }

        var report = await svc.GetReportAsync(Lead(), topN: 1);

        // Both agents are anomalous, but topN keeps only the most severe.
        Assert.Single(report.Anomalies);
        Assert.Equal("red1", report.Anomalies[0].AgentId);
        // The full roster is unaffected by topN.
        Assert.Equal(2, report.AllAgents.Count);
    }

    [Fact]
    public async Task GetReportAsync_UsesOnlyNewestWindowMeetings()
    {
        using var ctx = new SqliteTestContext();
        // Coherent(2,1,2) -> window=2, yellow=1, red=2.
        var svc = Build(ctx, 2, 1, 2);

        using (var db = ctx.NewContext())
        {
            db.Meetings.Add(Meeting("old", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("mid", new DateTime(2026, 2, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));
            db.Meetings.Add(Meeting("new", new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc), closed: true));

            db.Users.Add(Seed.Agent("a", Rank.SpecialAgent, AgentStatus.Active));

            // A miss in the oldest meeting must fall outside the two-meeting window.
            db.MeetingAttendances.Add(Att("old", "a", MeetingAttendanceStatus.Missing));
            db.MeetingAttendances.Add(Att("mid", "a", MeetingAttendanceStatus.Present));
            db.MeetingAttendances.Add(Att("new", "a", MeetingAttendanceStatus.Present));

            db.SaveChanges();
        }

        var report = await svc.GetReportAsync(Lead());

        Assert.Equal(2, report.WindowSize);
        Assert.Equal(2, report.MeetingsEvaluated); // only the two newest closed meetings

        var row = Assert.Single(report.AllAgents);
        Assert.Equal(2, row.Evaluated);
        Assert.Equal(2, row.Present);
        Assert.Equal(0, row.Missing); // the pre-window miss was excluded
        Assert.Equal(AttendanceAnomalyLevel.None, row.Level);
    }

    // ---------- absences ----------

    [Fact]
    public async Task GetReportAsync_AggregatesAbsenceCategoriesAndOpenAcknowledgement()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        var day = new DateOnly(2026, 6, 15);
        using (var db = ctx.NewContext())
        {
            db.Absences.Add(Abs("a", day, AbsenceCategory.Vacation, acknowledged: false));
            db.Absences.Add(Abs("a", day, AbsenceCategory.Vacation, acknowledged: true));
            db.Absences.Add(Abs("b", day, AbsenceCategory.Sick, acknowledged: false));
            db.Absences.Add(Abs("c", day, AbsenceCategory.Work, acknowledged: false));
            db.SaveChanges();
        }

        var report = await svc.GetReportAsync(Lead());

        // Three of the four are unacknowledged.
        Assert.Equal(3, report.AbsencesOpenAcknowledgement);

        Assert.Equal(2, Seg(report.AbsencesByCategory, AbsenceCategoryDisplay.Name(AbsenceCategory.Vacation)));
        Assert.Equal(1, Seg(report.AbsencesByCategory, AbsenceCategoryDisplay.Name(AbsenceCategory.Work)));
        Assert.Equal(1, Seg(report.AbsencesByCategory, AbsenceCategoryDisplay.Name(AbsenceCategory.Sick)));
        Assert.Equal(0, Seg(report.AbsencesByCategory, AbsenceCategoryDisplay.Name(AbsenceCategory.RpBreak)));
        Assert.Equal(0, Seg(report.AbsencesByCategory, AbsenceCategoryDisplay.Name(AbsenceCategory.Misc)));
    }

    // ---------- twelve-month time series ----------

    [Fact]
    public async Task GetReportAsync_TimeSeriesHasTwelveMonths_BucketsRecentExcludesOld()
    {
        using var ctx = new SqliteTestContext();
        var svc = Build(ctx, 5, 2, 3);

        // Align the "recent" bucket with the service's local-clock month.
        var nowLocal = MeetingTime.Local(DateTime.UtcNow);
        var today = DateOnly.FromDateTime(nowLocal);

        using (var db = ctx.NewContext())
        {
            db.Users.Add(Seed.Agent("a", Rank.SpecialAgent, AgentStatus.Active));

            // A closed meeting "now" with a miss -> current-month missing bucket.
            db.Meetings.Add(Meeting("mNow", DateTime.UtcNow, closed: true));
            db.MeetingAttendances.Add(Att("mNow", "a", MeetingAttendanceStatus.Missing));

            // One recent absence (counted) and one two years back (out of window).
            db.Absences.Add(Abs("a", today, AbsenceCategory.Vacation, acknowledged: true));
            db.Absences.Add(Abs("a", today.AddYears(-2), AbsenceCategory.Vacation, acknowledged: true));

            db.SaveChanges();
        }

        var report = await svc.GetReportAsync(Lead());

        Assert.Equal(12, report.TimeSeries.Count);
        Assert.Equal(12, report.AbsenceCountsPerMonth.Count);
        Assert.Equal(12, report.MissingCountsPerMonth.Count);

        // Last bucket is the current local month.
        Assert.Equal(nowLocal.Year, report.TimeSeries[11].Year);
        Assert.Equal(nowLocal.Month, report.TimeSeries[11].Month);

        // Only the recent absence and the recent miss land in the series, in the last bucket.
        Assert.Equal(1, report.AbsenceCountsPerMonth.Sum());
        Assert.Equal(1, report.AbsenceCountsPerMonth[11]);
        Assert.Equal(1, report.MissingCountsPerMonth.Sum());
        Assert.Equal(1, report.MissingCountsPerMonth[11]);
    }
}
