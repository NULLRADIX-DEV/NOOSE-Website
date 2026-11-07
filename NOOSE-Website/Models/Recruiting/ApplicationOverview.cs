using MudBlazor;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;

namespace NOOSE_Website.Models.Recruiting;

/// <summary>Tabs of the HRB application list, in display order.</summary>
public enum ApplicationListTab
{
    Eingang = 0,
    Sicherheitspruefung = 1,
    TestAusstehend = 2,
    TestAbsolviert = 3,
    Auswahlverfahren = 4,
    Archiv = 5,
    Alle = 6,
}

/// <summary>Where an applicant's test stands, as the list shows it.</summary>
public enum ApplicationTestStage
{
    None = 0,
    NotStarted = 1,
    Running = 2,
    Expired = 3,
    ToGrade = 4,
    Passed = 5,
    Failed = 6,
    Graded = 7,
}

/// <summary>Filter on the applicant's answer to whether the application may be streamed.</summary>
public enum ApplicationStreamFilter
{
    Alle = 0,
    Erlaubt = 1,
    NichtErlaubt = 2,
    KeineAngabe = 3,
}

/// <summary>Tab and test classification for the application list.</summary>
/// <remarks>No bUnit in the suite, so the branching lives here where it can be tested.</remarks>
public static class ApplicationOverview
{
    public static readonly IReadOnlyList<ApplicationListTab> Tabs = Enum.GetValues<ApplicationListTab>();

    public static string TabName(ApplicationListTab tab) => tab switch
    {
        ApplicationListTab.Eingang => "Eingang",
        ApplicationListTab.Sicherheitspruefung => "Sicherheitsüberprüfung",
        ApplicationListTab.TestAusstehend => "Test ausstehend",
        ApplicationListTab.TestAbsolviert => "Test absolviert",
        ApplicationListTab.Auswahlverfahren => "Auswahlverfahren",
        ApplicationListTab.Archiv => "Archiv",
        ApplicationListTab.Alle => "Alle",
        _ => "—",
    };

    /// <summary>The one tab besides "Alle" an application belongs to.</summary>
    public static ApplicationListTab TabFor(BewerbungStatus status, ITestAttempt? attempt, DateTime now) => status switch
    {
        BewerbungStatus.Eingereicht => ApplicationListTab.Eingang,
        BewerbungStatus.InSicherheitspruefung => ApplicationListTab.Sicherheitspruefung,
        BewerbungStatus.ImTest => IsDone(attempt, now) ? ApplicationListTab.TestAbsolviert : ApplicationListTab.TestAusstehend,
        BewerbungStatus.ImVorstellungsgespraech => ApplicationListTab.Auswahlverfahren,
        _ => ApplicationListTab.Archiv,
    };

    public static bool InTab(ApplicationListTab tab, BewerbungStatus status, ITestAttempt? attempt, DateTime now)
        => tab == ApplicationListTab.Alle || TabFor(status, attempt, now) == tab;

    /// <summary>Same tab for an application holding several tests; "absolviert" only once every one is done.</summary>
    public static ApplicationListTab TabForTests(BewerbungStatus status, IReadOnlyList<ITestAttempt>? attempts, DateTime now)
        => status == BewerbungStatus.ImTest
            ? attempts is { Count: > 0 } && attempts.All(a => IsDone(a, now))
                ? ApplicationListTab.TestAbsolviert
                : ApplicationListTab.TestAusstehend
            : TabFor(status, null, now);

    public static bool InTabTests(ApplicationListTab tab, BewerbungStatus status, IReadOnlyList<ITestAttempt>? attempts, DateTime now)
        => tab == ApplicationListTab.Alle || TabForTests(status, attempts, now) == tab;

    /// <summary>At least one handed-in test still waits for its grade.</summary>
    public static bool AnyToGrade(IReadOnlyList<ITestAttempt>? attempts, DateTime now)
        => attempts is not null && attempts.Any(a => TestStage(a, now) == ApplicationTestStage.ToGrade);

    public static string StreamFilterName(ApplicationStreamFilter filter) => filter switch
    {
        ApplicationStreamFilter.Erlaubt => "Stream erlaubt",
        ApplicationStreamFilter.NichtErlaubt => "Stream nicht erlaubt",
        ApplicationStreamFilter.KeineAngabe => "Keine Angabe",
        _ => "Alle",
    };

    /// <summary>Older applications predate the question and carry no answer.</summary>
    public static bool MatchesStream(ApplicationStreamFilter filter, bool? consent) => filter switch
    {
        ApplicationStreamFilter.Erlaubt => consent == true,
        ApplicationStreamFilter.NichtErlaubt => consent == false,
        ApplicationStreamFilter.KeineAngabe => consent is null,
        _ => true,
    };

    public static ApplicationTestStage TestStage(ITestAttempt? attempt, DateTime now)
    {
        if (attempt is null)
        {
            return ApplicationTestStage.None;
        }
        if (attempt.GradedAt is not null)
        {
            return Passed(attempt) switch
            {
                true => ApplicationTestStage.Passed,
                false => ApplicationTestStage.Failed,
                null => ApplicationTestStage.Graded,
            };
        }
        if (attempt.CompletedAt is not null)
        {
            return ApplicationTestStage.ToGrade;
        }
        return State(attempt, now) switch
        {
            TestAttemptState.Running => ApplicationTestStage.Running,
            TestAttemptState.Expired => ApplicationTestStage.Expired,
            _ => ApplicationTestStage.NotStarted,
        };
    }

    public static string TestLabel(ITestAttempt? attempt, DateTime now)
    {
        var stage = TestStage(attempt, now);
        return stage switch
        {
            ApplicationTestStage.None => "Kein Test",
            ApplicationTestStage.NotStarted => "Nicht begonnen",
            ApplicationTestStage.Running => "Läuft",
            ApplicationTestStage.Expired => "Zeit abgelaufen",
            ApplicationTestStage.ToGrade => attempt!.TimedOut ? "Zeit abgelaufen · zu bewerten" : "Abgegeben · zu bewerten",
            ApplicationTestStage.Passed => $"Bestanden · {Percent(attempt!)} %",
            ApplicationTestStage.Failed => $"Nicht bestanden · {Percent(attempt!)} %",
            ApplicationTestStage.Graded => $"Bewertet · {Percent(attempt!)} %",
            _ => "—",
        };
    }

    public static Color TestColor(ApplicationTestStage stage) => stage switch
    {
        ApplicationTestStage.Running => Color.Info,
        ApplicationTestStage.Expired or ApplicationTestStage.ToGrade => Color.Warning,
        ApplicationTestStage.Passed => Color.Success,
        ApplicationTestStage.Failed => Color.Error,
        ApplicationTestStage.Graded => Color.Primary,
        _ => Color.Default,
    };

    /// <summary>Handed in, or out of time; either way nothing more comes from the applicant.</summary>
    private static bool IsDone(ITestAttempt? attempt, DateTime now)
        => attempt is not null && State(attempt, now) is TestAttemptState.Submitted or TestAttemptState.Expired;

    private static TestAttemptState State(ITestAttempt a, DateTime now)
        => TestAttemptLogic.State(a.StartedAt, a.DeadlineAt, a.CompletedAt, a.TimedOut, now);

    private static int Percent(ITestAttempt a)
        => a.FinalMaxPoints is > 0 ? (int)Math.Round(100.0 * (a.FinalPoints ?? 0) / a.FinalMaxPoints.Value) : 0;

    private static bool? Passed(ITestAttempt a)
        => a.FinalPassPercent is { } limit ? Percent(a) >= limit : null;
}
