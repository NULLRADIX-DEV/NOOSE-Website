using Microsoft.EntityFrameworkCore;
using NOOSE_Website.Data;
using NOOSE_Website.Data.Entities.Recruiting;
using NOOSE_Website.Models.Abstractions;
using NOOSE_Website.Models.Enums;
using NOOSE_Website.Models.Recruiting;

namespace NOOSE_Website.Services;

/// <summary>A test definition as loaded for one attempt: the test, its questions and their options.</summary>
public sealed record TestTemplate(
    BewerbungTest? Test,
    IReadOnlyList<BewerbungTestQuestion> Questions,
    IReadOnlyList<BewerbungTestOption> Options);

/// <summary>Evaluation, questionnaire and answer upsert, shared by applicant and agent tests.</summary>
/// <remarks>
/// Pure apart from <see cref="LoadTemplateAsync"/>: each service loads its own answer rows and owns its guard,
/// its audit rows and its notifications. Written once so the two test kinds cannot grade the same answer
/// differently.
/// </remarks>
public static class TestEngine
{
    /// <summary>The test with its questions and options, both in display order.</summary>
    public static async Task<TestTemplate> LoadTemplateAsync(AppDbContext db, string testId,
        CancellationToken cancellationToken)
    {
        var test = await db.BewerbungTests.AsNoTracking().FirstOrDefaultAsync(t => t.Id == testId, cancellationToken);
        var questions = await db.BewerbungTestQuestions.AsNoTracking()
            .Where(q => q.TestId == testId).OrderBy(q => q.Sorting).ToListAsync(cancellationToken);
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await db.BewerbungTestOptions.AsNoTracking()
            .Where(o => questionIds.Contains(o.QuestionId)).OrderBy(o => o.Sorting).ToListAsync(cancellationToken);
        return new TestTemplate(test, questions, options);
    }

    /// <summary>One answer row per question, chosen the same way by every reader.</summary>
    /// <remarks>The index on (Zuweisung, Frage) is not unique, so a duplicate row is possible. Ordering it
    /// away means the grader always edits the row the evaluation reads.</remarks>
    public static Dictionary<string, T> PickAnswers<T>(IEnumerable<T> answers) where T : ITestAnswer
        => answers
            .OrderBy(a => a.CreatedAt).ThenBy(a => a.Id, StringComparer.Ordinal)
            .GroupBy(a => a.QuestionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    /// <summary>The grading view of a completed attempt; the guard is the caller's.</summary>
    public static TestEvaluation Evaluate<T>(TestTemplate template, IEnumerable<T> answers, ITestAttempt attempt)
        where T : ITestAnswer
    {
        var byQuestion = PickAnswers(answers);
        var items = new List<TestEvaluationItem>();
        var total = 0;
        var max = 0;
        foreach (var q in template.Questions)
        {
            max += q.Points;
            byQuestion.TryGetValue(q.Id, out var answer);
            string? answerText = null;
            bool? autoCorrect = null;
            string? correctAnswer = null;
            IReadOnlyList<string> matched = Array.Empty<string>();
            IReadOnlyList<string> missed = Array.Empty<string>();

            switch (q.Type)
            {
                case TestQuestionType.MultipleChoice:
                    var own = template.Options.Where(o => o.QuestionId == q.Id).ToList();
                    var chosenIds = TestGrading.SplitOptionIds(answer?.SelectedOptionIds, answer?.SelectedOptionId);
                    var chosen = own.Where(o => chosenIds.Contains(o.Id, StringComparer.Ordinal)).ToList();
                    answerText = chosen.Count == 0 ? null : string.Join(", ", chosen.Select(o => o.Label));
                    // the SHAPE OF THE STORED ANSWER decides the overload, not the question's current switch:
                    // turning Mehrfachauswahl off after the submission would otherwise grade a two-tick answer on
                    // whichever single option happened to come first
                    autoCorrect = q.AllowMultiple || chosenIds.Count > 1
                        ? TestGrading.GradeMultipleChoice(chosenIds, own)
                        : TestGrading.GradeMultipleChoice(chosen.FirstOrDefault());
                    if (!own.Any(o => o.IsCorrect))
                    {
                        // no option flagged: the machine cannot decide this one, and "wrong" would let
                        // the grading be closed on a 0 that nobody ever looked at
                        autoCorrect = null;
                    }
                    var correct = own.Where(o => o.IsCorrect).Select(o => o.Label).ToList();
                    correctAnswer = correct.Count > 0 ? string.Join(", ", correct) : null;
                    break;

                case TestQuestionType.YesNo:
                    answerText = answer?.FreeTextAnswer;
                    autoCorrect = TestGrading.GradeYesNo(answerText, q.CorrectYesNo);
                    correctAnswer = q.CorrectYesNo is null ? null : (q.CorrectYesNo.Value ? "Ja" : "Nein");
                    break;

                default: // FreeText
                    answerText = answer?.FreeTextAnswer;
                    (autoCorrect, matched, missed) = TestGrading.GradeFreeText(answerText, q.Keywords, q.MinKeywordHits);
                    break;
            }

            var effective = answer?.ManualCorrect ?? autoCorrect;
            // clamped on read as well as on write: the column is data, not a promise
            var awarded = answer?.ManualPoints is int manual
                ? Math.Clamp(manual, 0, q.Points)
                : effective == true ? q.Points : 0;
            total += awarded;

            items.Add(new TestEvaluationItem(
                answer?.Id, q.Id, q.Type, q.Prompt, answerText,
                autoCorrect, answer?.ManualCorrect, answer?.ManualPoints, effective,
                q.Points, awarded, correctAnswer, matched, missed));
        }

        // once grading is closed the headline figures are the frozen ones: editing the test afterwards must not
        // rewrite a verdict that was already communicated
        return new TestEvaluation(
            template.Test?.Title ?? "Test", attempt.CompletedAt,
            attempt.GradedAt is null ? total : attempt.FinalPoints ?? total,
            attempt.GradedAt is null ? max : attempt.FinalMaxPoints ?? max,
            // the threshold is frozen too: Passed is derived from it
            attempt.GradedAt is null ? template.Test?.PassPercent : attempt.FinalPassPercent,
            attempt.GradedAt, attempt.GradedByName, items);
    }

    /// <summary>The questionnaire with the saved draft folded in; carries no key and no verdict.</summary>
    /// <param name="seed">Shuffle seed; includes the attempt so a reset does not repeat the order.</param>
    public static IReadOnlyList<TestQuestionView> BuildQuestionViews<T>(TestTemplate template, IEnumerable<T> draft,
        string seed) where T : ITestAnswer
    {
        var saved = PickAnswers(draft);
        return template.Questions.Select(q =>
        {
            saved.TryGetValue(q.Id, out var own);
            return new TestQuestionView(
                q.Id, q.Type, q.Prompt, q.Required, q.AllowMultiple,
                TestGrading.SplitOptionIds(own?.SelectedOptionIds, own?.SelectedOptionId),
                own?.FreeTextAnswer,
                TestOptionOrder.For(seed, q.KeepOptionOrder, template.Options.Where(o => o.QuestionId == q.Id))
                    .Select(o => new TestOptionView(o.Id, o.Label)).ToList());
        }).ToList();
    }

    /// <summary>Question shape the upsert validates against.</summary>
    public sealed record QuestionShape(string Id, TestQuestionType Type, bool Required, bool AllowMultiple);

    /// <summary>Question and option shape of one test, for the answer upsert.</summary>
    public static async Task<(List<QuestionShape> Questions, ILookup<string, string> OptionIds)> LoadShapeAsync(
        AppDbContext db, string testId, CancellationToken cancellationToken)
    {
        var questions = await db.BewerbungTestQuestions.AsNoTracking()
            .Where(q => q.TestId == testId)
            .Select(q => new QuestionShape(q.Id, q.Type, q.Required, q.AllowMultiple))
            .ToListAsync(cancellationToken);
        var questionIds = questions.Select(q => q.Id).ToList();
        var options = await db.BewerbungTestOptions.AsNoTracking()
            .Where(o => questionIds.Contains(o.QuestionId))
            .Select(o => new { o.Id, o.QuestionId })
            .ToListAsync(cancellationToken);
        return (questions, options.ToLookup(o => o.QuestionId, o => o.Id, StringComparer.Ordinal));
    }

    /// <summary>Writes one answer row per question, creating what is missing; the guard is the caller's.</summary>
    /// <remarks>
    /// Shared by the draft and the submission: two write paths, one revalidation. The payload is never trusted
    /// here either - a foreign question id, an option belonging to another question and a duplicate row all
    /// have to die in this method, because the columns carry no foreign key that would catch an invented id.
    /// Upsert, not insert: the draft already left a row per question, and a second row would let the grader
    /// award points on a row the evaluation ignores. The two grading columns are never touched.
    /// </remarks>
    /// <param name="create">Creates and tracks a fresh row for a question id.</param>
    public static void ApplyAnswers<T>(IReadOnlyList<QuestionShape> questions, ILookup<string, string> optionIds,
        IReadOnlyList<T> existing, IReadOnlyList<TestAnswerInput> answers, bool requireMandatory,
        Func<string, T> create) where T : ITestAnswer
    {
        // last wins, so a replayed payload cannot produce two rows for one question
        var submitted = answers
            .Where(a => !string.IsNullOrWhiteSpace(a.QuestionId))
            .GroupBy(a => a.QuestionId, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.Ordinal);

        foreach (var question in questions)
        {
            submitted.TryGetValue(question.Id, out var input);
            var choice = new List<string>();
            string? freeText = null;

            if (question.Type == TestQuestionType.MultipleChoice)
            {
                var allowed = optionIds[question.Id].ToHashSet(StringComparer.Ordinal);
                choice = (input?.SelectedOptionIds ?? [])
                    .Where(allowed.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                if (!question.AllowMultiple && choice.Count > 1)
                {
                    choice = [choice[0]];
                }
            }
            else
            {
                freeText = Trim(input?.FreeText);
            }

            if (requireMandatory && question.Required && choice.Count == 0 && freeText is null)
            {
                throw new InvalidOperationException("Bitte beantworte alle Pflichtfragen.");
            }

            // a row per question even when it stays empty: the evaluation keys on it, and a missing row reads
            // as "question added later" rather than "left blank"
            var rows = existing.Where(a => a.QuestionId == question.Id).ToList();
            if (rows.Count == 0)
            {
                rows = [create(question.Id)];
            }
            // every row of the group, so a duplicate cannot drift away from the one that is read
            foreach (var row in rows)
            {
                row.SelectedOptionId = choice.Count == 1 ? choice[0] : null;
                row.SelectedOptionIds = TestGrading.JoinOptionIds(choice);
                row.FreeTextAnswer = freeText;
            }
        }
    }

    public static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
