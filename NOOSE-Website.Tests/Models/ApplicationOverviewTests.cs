using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Tests.Models;

/// <summary>Which tab an application lands in and what its test chip says.</summary>
public sealed class ApplicationOverviewTests
{
    private static readonly DateTime Now = new(2026, 11, 6, 12, 0, 0, DateTimeKind.Utc);

    private static BewerbungTestAssignment Attempt(DateTime? started = null, DateTime? deadline = null,
        DateTime? completed = null, bool timedOut = false, DateTime? graded = null,
        int? points = null, int? max = null, int? pass = null)
        => new()
        {
            StartedAt = started, DeadlineAt = deadline, CompletedAt = completed, TimedOut = timedOut,
            GradedAt = graded, FinalPoints = points, FinalMaxPoints = max, FinalPassPercent = pass,
        };

    [Theory]
    [InlineData(BewerbungStatus.Eingereicht, ApplicationListTab.Eingang)]
    [InlineData(BewerbungStatus.InSicherheitspruefung, ApplicationListTab.Sicherheitspruefung)]
    [InlineData(BewerbungStatus.ImVorstellungsgespraech, ApplicationListTab.Auswahlverfahren)]
    [InlineData(BewerbungStatus.Angenommen, ApplicationListTab.Archiv)]
    [InlineData(BewerbungStatus.Abgelehnt, ApplicationListTab.Archiv)]
    [InlineData(BewerbungStatus.Geschlossen, ApplicationListTab.Archiv)]
    public void TabFor_followsTheStatus(BewerbungStatus status, ApplicationListTab expected)
        => Assert.Equal(expected, ApplicationOverview.TabFor(status, null, Now));

    [Fact]
    public void TabFor_testStage_withoutAnAttempt_isPending()
        => Assert.Equal(ApplicationListTab.TestAusstehend,
            ApplicationOverview.TabFor(BewerbungStatus.ImTest, null, Now));

    [Fact]
    public void TabFor_testStage_whileRunning_isPending()
        => Assert.Equal(ApplicationListTab.TestAusstehend,
            ApplicationOverview.TabFor(BewerbungStatus.ImTest, Attempt(Now.AddMinutes(-5), Now.AddMinutes(5)), Now));

    [Fact]
    public void TabFor_testStage_onceHandedIn_isDone()
        => Assert.Equal(ApplicationListTab.TestAbsolviert,
            ApplicationOverview.TabFor(BewerbungStatus.ImTest, Attempt(Now.AddMinutes(-20), completed: Now), Now));

    [Fact]
    public void TabFor_testStage_pastTheDeadline_isDone_evenBeforeTheWorkerClosedIt()
        => Assert.Equal(ApplicationListTab.TestAbsolviert,
            ApplicationOverview.TabFor(BewerbungStatus.ImTest, Attempt(Now.AddMinutes(-40), Now.AddMinutes(-1)), Now));

    [Fact]
    public void InTab_alle_holdsEverything()
        => Assert.True(ApplicationOverview.InTab(ApplicationListTab.Alle, BewerbungStatus.Abgelehnt, null, Now));

    [Fact]
    public void TestLabel_withoutAttempt()
        => Assert.Equal("Kein Test", ApplicationOverview.TestLabel(null, Now));

    [Fact]
    public void TestLabel_handedIn_waitsForGrading()
    {
        var a = Attempt(Now.AddMinutes(-20), completed: Now);
        Assert.Equal(ApplicationTestStage.ToGrade, ApplicationOverview.TestStage(a, Now));
        Assert.Equal("Abgegeben · zu bewerten", ApplicationOverview.TestLabel(a, Now));
    }

    [Fact]
    public void TestLabel_closedByTheClock_saysSo()
        => Assert.Equal("Zeit abgelaufen · zu bewerten",
            ApplicationOverview.TestLabel(Attempt(Now.AddMinutes(-40), completed: Now, timedOut: true), Now));

    [Fact]
    public void TestLabel_graded_showsVerdictAndPercent()
    {
        var passed = Attempt(completed: Now, graded: Now, points: 17, max: 20, pass: 70);
        var failed = Attempt(completed: Now, graded: Now, points: 9, max: 20, pass: 70);
        Assert.Equal("Bestanden · 85 %", ApplicationOverview.TestLabel(passed, Now));
        Assert.Equal("Nicht bestanden · 45 %", ApplicationOverview.TestLabel(failed, Now));
    }

    [Fact]
    public void TestLabel_gradedWithoutThreshold_hasNoVerdict()
        => Assert.Equal("Bewertet · 50 %",
            ApplicationOverview.TestLabel(Attempt(completed: Now, graded: Now, points: 5, max: 10), Now));

    [Theory]
    [InlineData(ApplicationStreamFilter.Alle, null, true)]
    [InlineData(ApplicationStreamFilter.Alle, false, true)]
    [InlineData(ApplicationStreamFilter.Erlaubt, true, true)]
    [InlineData(ApplicationStreamFilter.Erlaubt, null, false)]
    [InlineData(ApplicationStreamFilter.NichtErlaubt, false, true)]
    [InlineData(ApplicationStreamFilter.NichtErlaubt, true, false)]
    [InlineData(ApplicationStreamFilter.KeineAngabe, null, true)]
    [InlineData(ApplicationStreamFilter.KeineAngabe, false, false)]
    public void MatchesStream_filtersOnTheAnswer(ApplicationStreamFilter filter, bool? consent, bool expected)
        => Assert.Equal(expected, ApplicationOverview.MatchesStream(filter, consent));
}
