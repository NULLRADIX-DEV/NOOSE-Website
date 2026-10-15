using NOOSE_Website.Models.Recruiting;
using NOOSE_Website.Services;
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services;

/// <summary>Structural guard: an applicant must learn nothing about their aptitude-test outcome.</summary>
/// <remarks>
/// Three layers, because each covers a different way the rule dies. The DTO shape lock stops the verdict from
/// ever being available on the applicant surface. The file scan stops a well-meaning "tell them how they did"
/// edit. The service-gate scan stops a new read path from shipping without a guard. All three are green as
/// written — this freezes behaviour, it does not demand a change.
/// </remarks>
public class RecruitingTestVisibilityTests
{
    private const string Why =
        "Ein Bewerber darf NICHTS über sein Testergebnis erfahren: nicht die Punktzahl, nicht die " +
        "Bestehensgrenze, nicht welche Frage falsch war, nicht den Lösungsschlüssel, und möglichst nicht " +
        "einmal, ob er bestanden hat. Die BEWERBUNGS-Entscheidung (angenommen/abgelehnt) darf sichtbar " +
        "sein — das Testurteil nicht.\n" +
        "Diese Symbole tragen das Urteil oder den Schlüssel. Auf der Bewerber-Oberfläche stehen sie " +
        "baulich nicht zur Verfügung: TestView/TestQuestionView/TestOptionView können sie nicht liefern, " +
        "TestEvaluation entsteht nur hinter Permission.RequireHrbOrLeadership.\n" +
        "Willst du dem Bewerber etwas Neues zeigen, projiziere es durch ein eigenes, geschwärztes DTO — " +
        "binde nicht die Rohentität und nicht BewerbungssperreInfo.Reason.\n" +
        "Betrifft es BewerbungMessagePanel (mit der HRB-Seite geteilt): Komponente aufteilen, nicht diese " +
        "Regel lockern.\nTreffer:\n";

    /// <summary>Identifiers carrying the verdict, the answer key, or HRB decision prose. Case-sensitive.</summary>
    private static readonly string[] CodeSymbols =
    [
        "TestEvaluation", "TestEvaluationItem", "GetEvaluationAsync", "TestGrading",
        "SetAwardedPointsAsync", "CompleteGradingAsync", "ReopenGradingAsync", "ManualPoints",
        "GradedAt", "GradedByName", "FinalPoints", "FinalMaxPoints", "OpenCount", "IsGraded",
        "TestEditModel", "TestQuestionEdit",
        "PassPercent", "Bestehensgrenze", "TotalPoints", "MaxPoints", "AwardedPoints", "Passed", "Punkte",
        "IsCorrect", "IstRichtig", "CorrectAnswer", "CorrectYesNo", "RichtigJaNein",
        "Keywords", "Schlagwoerter", "MinKeywordHits", "MindestTreffer", "MatchedKeywords", "MissedKeywords",
        "ManualCorrect", "ManuellRichtig", "AutoCorrect", "EffectiveCorrect",
        "DecisionNote", "Entscheidungsnotiz", "SecurityCheckPassed", "SicherheitBestanden",
        "Reason", "CreatedByName",
    ];

    /// <summary>A verdict does not need an identifier to leak; a sentence will do. Case-insensitive.</summary>
    /// <remarks>Richtig needs word boundaries so "Richtigkeit meiner Angaben" stays legal.</remarks>
    private static readonly string[] ProseSymbols =
    [
        "bestanden", "durchgefallen", "Punktzahl", "Testergebnis", "Bewertung", "Prozent", "Richtig",
    ];

    /// <summary>Rendered inside the applicant portal but living outside Pages/Portal.</summary>
    private static readonly string[] DeclaredSurface =
    [
        Path.Combine("Layout", "ApplicantPortalLayout.razor"),
        Path.Combine("Pages", "Recruiting", "Shared", "BewerbungssperreCard.razor"),
        Path.Combine("Pages", "Recruiting", "Shared", "BewerbungMessagePanel.razor"),
        // the bell renders the applicant's OWN notifications - a title and a link, keyed on their agent id, with
        // no verdict among them. Declared rather than treated as inert, so it is scanned like everything else.
        Path.Combine("Layout", "NotificationBell.razor"),
        // the shells were unified: the applicant portal now renders the same header and tab bar as the public
        // site, so both are scanned here rather than trusted
        Path.Combine("Common", "Shared", "PublicHeader.razor"),
        Path.Combine("Layout", "PublicNav.razor"),
        // the countdown bar of a running attempt: it renders the remaining time and the save state, both of
        // which are the applicant's own clock and their own input. Declared rather than inert, so it is
        // scanned like everything else on the surface.
        Path.Combine("Pages", "Portal", "Shared", "PruefungStatusBar.razor"),
        // the questionnaire of a running attempt, shared with the internal test page: prompts, options and the
        // taker's own typed answers, never a key or a verdict. Declared so it is scanned like the page was.
        Path.Combine("Pages", "Portal", "Shared", "TestQuestionnaire.razor"),
    ];

    /// <summary>Framework and inert tags that need no decision.</summary>
    private static readonly string[] InertTags =
        ["PageTitle", "InputFile", "AntiforgeryToken", "ImageLightbox", "LegalFooter", "AuthorizeView", "Authorized", "NotAuthorized"];

    private static readonly Regex CodeRegex =
        new(@"\b(?:" + string.Join("|", CodeSymbols) + @")\b", RegexOptions.Compiled);

    private static readonly Regex ProseRegex =
        new(@"\b(?:" + string.Join("|", ProseSymbols) + @")\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string ComponentRoot([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "Components"));

    private static string ServiceFile([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website", "Services", "BewerbungTestService.cs"));

    /// <summary>The pages an applicant actually sees: the ones wearing the applicant layout.</summary>
    /// <remarks>
    /// Not every file under Pages/Portal — that folder also holds the citizen area (own tips, tickets, objection,
    /// reward receipt), which answers to BuergerLayout and has nothing to do with a recruiting test. Scanning it
    /// reported an objection's DecisionNote as a leaked test verdict. Keyed on the layout so a new applicant page is
    /// picked up on its own and a new citizen page is not.
    /// </remarks>
    private static List<string> ApplicantPortalPages(string root)
    {
        var portal = Path.Combine(root, "Pages", "Portal");
        Assert.True(Directory.Exists(portal), $"Bewerber-Portalordner nicht gefunden: {portal}");

        var files = Directory.EnumerateFiles(portal, "*.razor", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => File.ReadAllText(f).Contains("@layout ApplicantPortalLayout", StringComparison.Ordinal))
            .ToList();
        // the applicant portal exists; an empty result means the layout was renamed, not that there is nothing to guard
        Assert.NotEmpty(files);
        return files;
    }

    private static List<string> ApplicantSurface(string root)
    {
        var files = ApplicantPortalPages(root);

        foreach (var declared in DeclaredSurface)
        {
            var full = Path.Combine(root, declared);
            Assert.True(File.Exists(full), $"Erklärte Bewerber-Oberfläche nicht gefunden: {full}");
            files.Add(full);
        }
        return files;
    }

    // ---------- Layer A: the applicant DTOs cannot carry a verdict ----------

    [Theory]
    // DeadlineAt/TimedOut/TimeLimitMinutes are the applicant's OWN attempt clock. They cannot encode a
    // verdict: when the deadline is written no answer exists yet, and running out of time is orthogonal to
    // being right — a timed-out applicant may have answered everything correctly.
    // AttemptNumber is how many times HRB released this test to them; they see the questionnaire either way,
    // and the write paths use it to refuse answers that were typed for a previous attempt.
    [InlineData(typeof(TestView),
        "AssignmentId,AttemptNumber,CaseNumber,Completed,DeadlineAt,Description,Questions,TimeLimitMinutes,TimedOut,Title")]
    // AllowMultiple is the shape of the form control, an author switch — it says nothing about the key: a
    // single-choice question can have one correct option and a multi-choice one can have several, or none.
    // SavedOptionIds/SavedFreeText are the applicant's own draft answer echoed back so a reload loses
    // nothing; both were already delivered to them, so no new information crosses.
    [InlineData(typeof(TestQuestionView),
        "AllowMultiple,Options,Prompt,QuestionId,Required,SavedFreeText,SavedOptionIds,Type")]
    [InlineData(typeof(TestOptionView), "Label,OptionId")]
    // the summary the status page reads; it deliberately carries no questions and no answers
    [InlineData(typeof(TestStatusView),
        "AssignmentId,Completed,DeadlineAt,Description,StartedAt,TimeLimitMinutes,TimedOut,Title")]
    public void TheApplicantFacingTestDtos_HaveExactlyTheseMembers(Type dto, string expected)
    {
        var actual = string.Join(",", dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).Order(StringComparer.Ordinal));

        Assert.True(expected == actual,
            "Die Bewerber-DTOs sind absichtlich urteilsfrei. Ein neues Mitglied hier ist der kürzeste Weg " +
            "zum Leck — es ist dann auf jeder Portalseite in Reichweite. Erweitern nur, wenn der Bewerber " +
            $"das Feld sehen DARF; dann diese Liste mit einer Begründung anpassen.\n{dto.Name}\n" +
            $"erwartet: {expected}\ntatsächlich: {actual}");
    }

    [Fact]
    public void NothingReachableFromTestView_CarriesAVerdictOrTheAnswerKey()
    {
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>([typeof(TestView)]);
        var offenders = new List<string>();

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type))
            {
                continue;
            }
            foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (CodeRegex.IsMatch(prop.Name))
                {
                    offenders.Add($"{type.Name}.{prop.Name}");
                }
                foreach (var next in Unwrap(prop.PropertyType).Where(Interesting))
                {
                    queue.Enqueue(next);
                }
            }
        }

        Assert.True(offenders.Count == 0, Why + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
        // the applicant object graph is three records and stays three records
        Assert.Equal(3, seen.Count(t => t.Namespace == "NOOSE_Website.Models.Recruiting"));
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        var bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare.IsArray && bare.GetElementType() is { } element)
        {
            yield return element;
            yield break;
        }
        if (bare.IsGenericType)
        {
            foreach (var arg in bare.GetGenericArguments())
            {
                yield return arg;
            }
            yield break;
        }
        yield return bare;
    }

    /// <summary>A component tag, not a C# generic argument.</summary>
    /// <remarks>The lookbehind is the whole trick: a markup tag's "&lt;" follows whitespace or "&gt;",
    /// while Task&lt;AuthenticationState&gt; or List&lt;TestAnswerInput&gt; always follows an identifier
    /// character. That lets the scan cover the @code block too, so a component built in a RenderFragment
    /// cannot slip past.</remarks>
    private const string TagPattern = @"(?<![A-Za-z0-9_])<([A-Z][A-Za-z0-9]*)";

    /// <summary>Only project types can hide a verdict; framework and scalar types cannot.</summary>
    private static bool Interesting(Type type)
        => !type.IsPrimitive && !type.IsEnum && type != typeof(string) && type != typeof(DateTime)
            && type != typeof(DateTimeOffset) && type != typeof(Guid) && type != typeof(decimal)
            && type != typeof(object) && !typeof(IEnumerable).IsAssignableFrom(type)
            && type.Namespace?.StartsWith("NOOSE_Website", StringComparison.Ordinal) == true;

    // ---------- Layer B: no verdict symbol on the applicant surface ----------

    [Fact]
    public void NoFileOnTheApplicantSurface_MentionsAVerdictOrTheAnswerKey()
    {
        var root = ComponentRoot();
        Assert.True(Directory.Exists(root), $"Komponentenordner nicht gefunden: {root}");

        var offenders = new List<string>();
        foreach (var file in ApplicantSurface(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var hits = CodeRegex.Matches(lines[i]).Select(m => m.Value)
                    .Concat(ProseRegex.Matches(lines[i]).Select(m => m.Value));
                foreach (var hit in hits)
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  {hit}");
                }
            }
        }

        Assert.True(offenders.Count == 0, Why + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void TheApplicantSurface_NeverRendersTheInternalStageLabel()
    {
        var root = ComponentRoot();
        // Name() and ChipColor() distinguish Test from Vorstellungsgespraech, which tells the applicant
        // the test was acceptable. ApplicantName()/ApplicantStep() merge both into "Auswahlverfahren".
        string[] internalCalls = ["BewerbungStatusDisplay.Name(", "BewerbungStatusDisplay.ChipColor(_"];

        var offenders = new List<string>();
        foreach (var file in ApplicantSurface(root))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (var call in internalCalls.Where(c => lines[i].Contains(c, StringComparison.Ordinal)))
                {
                    offenders.Add($"{Path.GetRelativePath(root, file)}:{i + 1}  {call}");
                }
            }
        }

        Assert.True(offenders.Count == 0,
            "Auf der Bewerber-Oberfläche darf nur ApplicantName()/ApplicantStep() gerendert werden. Name() " +
            "und ChipColor() unterscheiden die Test- von der Vorstellungsgespräch-Stufe — sobald das HRB " +
            "nach dem Test weiterschaltet, hat der Bewerber damit erfahren, dass er bestanden hat. Auch " +
            "die Chip-FARBE muss über ApplicantStep laufen, sonst verrät sie den Sprung, den das Label " +
            "versteckt.\n" + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void TheScannedSurface_IsTheApplicantPortalAndNothingElse()
    {
        // the guard of the guard: narrowing the scan is only safe while it still covers every applicant page and
        // still leaves the citizen area out. A renamed layout would silently shrink it to nothing otherwise.
        var files = ApplicantPortalPages(ComponentRoot()).Select(Path.GetFileName).ToArray();

        Assert.Contains("MeineBewerbung.razor", files);
        Assert.Contains("Status.razor", files);
        Assert.Contains("Test.razor", files);
        Assert.DoesNotContain("MeinEinspruch.razor", files);
        Assert.DoesNotContain("MeineTickets.razor", files);
    }

    [Fact]
    public void EveryComponentRenderedOnAPortalPage_IsPartOfTheDeclaredSurface()
    {
        var root = ComponentRoot();
        var known = DeclaredSurface.Select(Path.GetFileNameWithoutExtension)
            .Concat(InertTags).ToHashSet(StringComparer.Ordinal);

        var files = ApplicantPortalPages(root)
            .Append(Path.Combine(root, "Layout", "ApplicantPortalLayout.razor"));

        var undecided = files
            .SelectMany(f => Regex.Matches(File.ReadAllText(f), TagPattern)
                .Select(m => m.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Where(tag => !tag.StartsWith("Mud", StringComparison.Ordinal) && !known.Contains(tag))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.True(undecided.Length == 0,
            "Eine neue Komponente rendert auf einer Bewerberseite und ist noch nicht entschieden. Trage sie " +
            "in DeclaredSurface ein (dann wird sie mitgescannt) oder in InertTags (wenn sie nachweislich " +
            "keine Aktendaten rendert). Ungeprüft darf dort nichts stehen — genau so entstehen Lecks.\n" +
            string.Join("\n", undecided));
    }

    // ---------- Layer C: every read path is gated, before the first await ----------

    [Fact]
    public void EveryTestServiceMember_GuardsBeforeItTouchesTheDatabase()
    {
        var path = ServiceFile();
        Assert.True(File.Exists(path), $"Dienst nicht gefunden: {path}");
        var text = File.ReadAllText(path);

        string[] applicantMembers =
            ["GetAssignedForApplicantAsync", "GetTestStatusForApplicantAsync", "SaveDraftAsync", "SubmitAnswersAsync"];
        // extending or resetting a running attempt writes via ExecuteUpdateAsync, which the read-only barrier
        // never sees, so the rank check alone would let the read-only supervision and the demo principal
        // through. One combined guard, because every member may carry exactly one.
        string[] attemptWriteMembers = ["ExtendAttemptAsync", "ResetAttemptAsync"];
        // newer than the rest of the catalogue writes, so it carries the write check of its own
        string[] catalogWriteMembers = ["UpdateUsageAsync"];
        var members = typeof(IBewerbungTestService).GetMethods()
            .Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(members.Length >= 17, $"Unerwartet wenige Schnittstellen-Methoden: {members.Length}");

        var starts = Regex.Matches(text, @"(?m)^    (?:public|private|internal|protected)\b")
            .Select(m => m.Index).Append(text.Length).ToArray();

        var offenders = new List<string>();
        foreach (var name in members)
        {
            var expected = applicantMembers.Contains(name, StringComparer.Ordinal)
                ? "Permission.RequireApplicant("
                : attemptWriteMembers.Contains(name, StringComparer.Ordinal)
                    ? "Permission.RequireTestAttemptWrite("
                    : catalogWriteMembers.Contains(name, StringComparer.Ordinal)
                        ? "Permission.RequireHrbOrLeadershipWrite("
                        : "Permission.RequireHrbOrLeadership(";

            var regions = new List<string>();
            for (var i = 0; i < starts.Length - 1; i++)
            {
                var region = text[starts[i]..starts[i + 1]];
                // leading space so " AssignAsync(" cannot match "GetAssignedForApplicantAsync("
                if (region.Split('\n')[0].Contains($" {name}(", StringComparison.Ordinal))
                {
                    regions.Add(region);
                }
            }
            if (regions.Count != 1)
            {
                offenders.Add($"{name}: {regions.Count} Fundstellen im Dienst (erwartet genau eine)");
                continue;
            }

            var body = regions[0];
            var guard = body.IndexOf(expected, StringComparison.Ordinal);
            if (guard < 0)
            {
                offenders.Add($"{name}: {expected} fehlt");
                continue;
            }
            var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
            if (firstAwait >= 0 && guard > firstAwait)
            {
                offenders.Add($"{name}: Wächter steht nach dem ersten await");
            }
            var guardCount = Regex.Matches(body, @"Permission\.Require\w+").Count;
            if (guardCount != 1)
            {
                offenders.Add($"{name}: {guardCount} Permission.Require*-Aufrufe (erwartet genau einen)");
            }
        }

        Assert.True(offenders.Count == 0,
            "Jede Bauen-/Zuweisen-/Auswertungs-Methode ist dem HRB vorbehalten, jede Bewerber-Methode dem " +
            "Bewerber — und der Wächter steht VOR dem ersten await, damit vor dem Gate nichts gelesen wird. " +
            "Eine neue Schnittstellen-Methode ohne Wächter ist ein offener Lesepfad auf den " +
            $"Lösungsschlüssel.\n{string.Join("\n", offenders.Order(StringComparer.Ordinal))}");
    }
}
