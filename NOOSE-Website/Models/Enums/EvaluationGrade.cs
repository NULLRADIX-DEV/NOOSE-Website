using MudBlazor;

namespace NOOSE_Website.Models.Enums;

/// <summary>Grade of a service evaluation, the five steps of the service regulation; the value is the school grade.</summary>
public enum EvaluationGrade
{
    VeryGood = 1,
    Good = 2,
    Satisfactory = 3,
    Poor = 4,
    Insufficient = 5,
}

/// <summary>Display labels and chip colors.</summary>
public static class EvaluationGradeDisplay
{
    /// <summary>Every grade, best first.</summary>
    public static readonly IReadOnlyList<EvaluationGrade> All = new[]
    {
        EvaluationGrade.VeryGood,
        EvaluationGrade.Good,
        EvaluationGrade.Satisfactory,
        EvaluationGrade.Poor,
        EvaluationGrade.Insufficient,
    };

    /// <summary>The word alone, as the service regulation names it.</summary>
    public static string Word(EvaluationGrade grade) => grade switch
    {
        EvaluationGrade.VeryGood => "sehr gut",
        EvaluationGrade.Good => "gut",
        EvaluationGrade.Satisfactory => "befriedigend",
        EvaluationGrade.Poor => "mangelhaft",
        EvaluationGrade.Insufficient => "ungenügend",
        _ => "—",
    };

    /// <summary>Number and word, e.g. "2 – gut".</summary>
    public static string Name(EvaluationGrade grade)
        => Enum.IsDefined(grade) ? $"{(int)grade} – {Word(grade)}" : "—";

    public static Color ChipColor(EvaluationGrade grade) => grade switch
    {
        EvaluationGrade.VeryGood or EvaluationGrade.Good => Color.Success,
        EvaluationGrade.Satisfactory => Color.Info,
        EvaluationGrade.Poor => Color.Warning,
        EvaluationGrade.Insufficient => Color.Error,
        _ => Color.Default,
    };
}
