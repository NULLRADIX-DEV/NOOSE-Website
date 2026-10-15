using NOOSE_Website.Models.Tests;
using NOOSE_Website.Services;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace NOOSE_Website.Tests.Services;

/// <summary>Structural guard for internal tests: every member is gated, and the taker never reaches the key.</summary>
/// <remarks>Twin of <see cref="RecruitingTestVisibilityTests"/>. An agent may see points, percent and verdict of
/// their own graded test — unlike an applicant — but never which question was wrong and never the answer key.</remarks>
public class AgentTestVisibilityTests
{
    private static string Root([CallerFilePath] string here = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", "..", "NOOSE-Website"));

    /// <summary>Identifiers that carry the key or the per-question verdict. Case-sensitive.</summary>
    private static readonly string[] KeySymbols =
    [
        "TestEvaluation", "TestEvaluationItem", "GetEvaluationAsync", "TestGradingView", "CorrectAnswer",
        "CorrectYesNo", "IsCorrect", "Keywords", "MinKeywordHits", "AutoCorrect", "EffectiveCorrect", "ManualPoints",
        "AwardedPoints", "MatchedKeywords", "MissedKeywords",
    ];

    private static readonly Regex KeyRegex = new(@"\b(?:" + string.Join("|", KeySymbols) + @")\b", RegexOptions.Compiled);

    [Fact]
    public void Every_agent_test_member_guards_before_it_touches_the_database()
    {
        var path = Path.Combine(Root(), "Services", "AgentTestService.cs");
        var text = File.ReadAllText(path);

        string[] takerMembers =
            ["GetMineAsync", "GetMyStatusAsync", "OpenAsync", "SaveDraftAsync", "SubmitAsync", "GetMyResultAsync"];
        string[] readMembers =
            ["GetAssignableTestsAsync", "GetOverviewAsync", "GetForAgentAsync", "GetAssignmentAsync", "GetRowAsync", "GetEvaluationAsync"];
        var members = typeof(IAgentTestService).GetMethods().Select(m => m.Name).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(members.Length >= 19, $"Unerwartet wenige Schnittstellen-Methoden: {members.Length}");

        var starts = Regex.Matches(text, @"(?m)^    (?:public|private|internal|protected)\b")
            .Select(m => m.Index).Append(text.Length).ToArray();
        var offenders = new List<string>();
        foreach (var name in members)
        {
            var expected = takerMembers.Contains(name, StringComparer.Ordinal) ? "Permission.RequireTestTaker("
                : readMembers.Contains(name, StringComparer.Ordinal) ? "Permission.RequireHrbOrLeadership("
                : "Permission.RequireHrbOrLeadershipWrite(";

            var regions = new List<string>();
            for (var i = 0; i < starts.Length - 1; i++)
            {
                var region = text[starts[i]..starts[i + 1]];
                if (region.Split('\n')[0].Contains($" {name}(", StringComparison.Ordinal))
                {
                    regions.Add(region);
                }
            }
            if (regions.Count != 1)
            {
                offenders.Add($"{name}: {regions.Count} Fundstellen (erwartet genau eine)");
                continue;
            }
            var body = regions[0];
            var guard = body.IndexOf(expected, StringComparison.Ordinal);
            var firstAwait = body.IndexOf("await ", StringComparison.Ordinal);
            if (guard < 0)
            {
                offenders.Add($"{name}: {expected} fehlt");
            }
            else if (firstAwait >= 0 && guard > firstAwait)
            {
                offenders.Add($"{name}: Wächter steht nach dem ersten await");
            }
            var count = Regex.Matches(body, @"Permission\.Require\w+").Count;
            if (count != 1)
            {
                offenders.Add($"{name}: {count} Permission.Require*-Aufrufe (erwartet genau einen)");
            }
        }

        Assert.True(offenders.Count == 0,
            "Jede Methode der internen Tests trägt genau einen Wächter vor dem ersten await: Ablegen dem Agenten, "
            + "Lesen dem HRB/der Führung, Schreiben zusätzlich mit Schreibrecht.\n"
            + string.Join("\n", offenders.Order(StringComparer.Ordinal)));
    }

    [Theory]
    [InlineData(typeof(AgentTestView))]
    [InlineData(typeof(AgentTestStatusView))]
    [InlineData(typeof(AgentTestResult))]
    [InlineData(typeof(AgentTestRow))]
    public void The_taker_dtos_carry_no_key(Type dto)
    {
        var offenders = dto.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => KeyRegex.IsMatch(p.Name)).Select(p => p.Name).ToList();
        Assert.True(offenders.Count == 0, $"{dto.Name} trägt Lösung oder Einzelwertung: {string.Join(", ", offenders)}");
    }

    [Theory]
    [InlineData("Components/Pages/Tests/TestTake.razor")]
    [InlineData("Components/Pages/Tests/Shared/MyAgentTestsPanel.razor")]
    [InlineData("Components/Pages/Tests/Shared/AgentTestResultCard.razor")]
    [InlineData("Components/Pages/Portal/Shared/TestQuestionnaire.razor")]
    public void The_taker_surface_never_names_the_evaluation(string relative)
    {
        var path = Path.Combine(Root(), relative.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(path), $"Datei nicht gefunden: {path}");
        var hits = File.ReadAllLines(path)
            .Select((line, i) => (line, i))
            .SelectMany(x => KeyRegex.Matches(x.line).Select(m => $"{relative}:{x.i + 1}  {m.Value}"))
            .ToList();
        Assert.True(hits.Count == 0,
            "Die Seiten, auf denen ein Agent seinen Test ablegt oder sein Ergebnis sieht, dürfen die Auswertung nicht "
            + "erreichen: sie trägt den Lösungsschlüssel und je Frage richtig/falsch.\n" + string.Join("\n", hits));
    }
}
