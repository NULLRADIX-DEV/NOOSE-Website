using System.Text.Json;

namespace NOOSE_Website.Models.Recruiting;

/// <summary>One reliability question of the application form.</summary>
/// <param name="Key">Stable handle stored with the answer; never rename.</param>
/// <param name="Label">Short name for the staff view.</param>
/// <param name="Question">Wording the applicant sees.</param>
public sealed record ReliabilityQuestion(string Key, string Label, string Question);

/// <summary>The applicant's answer to one reliability question.</summary>
public sealed class ReliabilityAnswer
{
    public string Key { get; set; } = string.Empty;
    public bool? Answer { get; set; }
    public string? Details { get; set; }
}

/// <summary>The mandatory reliability questions of an application: checks, stores and reads the answers.</summary>
public static class ReliabilityQuestions
{
    public const int MaxDetailsLength = 1000;

    public static readonly IReadOnlyList<ReliabilityQuestion> All =
    [
        new("korruption", "Korruptionsvorwürfe",
            "Gibt es dokumentierte Korruptionsvorwürfe gegen dich?"),
        new("vermoegen", "Auffällige Vermögensverhältnisse",
            "Hast du auffällige Vermögensverhältnisse?"),
        new("schulden", "Ungewöhnliche Schulden",
            "Hast du ungewöhnliche Schulden?"),
        new("kriminelle-organisationen", "Beziehungen zu kriminellen Organisationen",
            "Hast du Beziehungen zu kriminellen Organisationen?"),
        new("nebentaetigkeiten", "Interessenkonflikte durch Nebentätigkeiten",
            "Hast du Interessenkonflikte durch Nebentätigkeiten?"),
        new("politik", "Politische Funktion oder Interesse",
            "Hast du eine politische Funktion oder ein politisches Interesse?"),
    ];

    /// <summary>One unanswered row per question, for a fresh form.</summary>
    public static List<ReliabilityAnswer> NewAnswers()
        => All.Select(q => new ReliabilityAnswer { Key = q.Key }).ToList();

    public static ReliabilityQuestion? Find(string key) => All.FirstOrDefault(q => q.Key == key);

    /// <summary>The first problem with the answers, or null when every question is answered and every yes explained.</summary>
    public static string? Validate(IEnumerable<ReliabilityAnswer>? answers)
    {
        var byKey = (answers ?? []).Where(a => a is not null).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        foreach (var question in All)
        {
            if (!byKey.TryGetValue(question.Key, out var answer) || answer.Answer is null)
            {
                return "Bitte beantworte alle Fragen zur Zuverlässigkeit.";
            }
            if (answer.Answer == true && string.IsNullOrWhiteSpace(answer.Details))
            {
                return $"Bitte gib bei „{question.Label}“ an, worum es geht.";
            }
        }
        return null;
    }

    /// <summary>JSON for the column, in question order; details only on a yes, trimmed and capped.</summary>
    public static string Serialize(IEnumerable<ReliabilityAnswer>? answers)
    {
        var byKey = (answers ?? []).Where(a => a is not null).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First());
        var clean = All
            .Where(q => byKey.ContainsKey(q.Key) && byKey[q.Key].Answer is not null)
            .Select(q =>
            {
                var a = byKey[q.Key];
                var details = a.Answer == true && !string.IsNullOrWhiteSpace(a.Details) ? Cut(a.Details.Trim()) : null;
                return new ReliabilityAnswer { Key = q.Key, Answer = a.Answer, Details = details };
            })
            .ToList();
        return JsonSerializer.Serialize(clean);
    }

    /// <summary>Reads the column; empty or broken JSON (applications from before the questions) gives an empty list.</summary>
    public static IReadOnlyList<ReliabilityAnswer> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }
        try
        {
            return JsonSerializer.Deserialize<List<ReliabilityAnswer>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>One line per answer, e.g. for the FIBI context.</summary>
    public static string Describe(ReliabilityAnswer a)
    {
        var label = Find(a.Key)?.Label ?? a.Key;
        var value = a.Answer switch { true => "ja", false => "nein", null => "keine Angabe" };
        return string.IsNullOrWhiteSpace(a.Details) ? $"{label}: {value}" : $"{label}: {value} ({a.Details})";
    }

    private static string Cut(string value) => value.Length <= MaxDetailsLength ? value : value[..MaxDetailsLength];
}
