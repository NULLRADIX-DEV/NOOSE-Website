namespace NOOSE_Website.Models.Recruiting;

/// <summary>One requirement shown on the public career page; alternatives render as "ODER" lines beneath it.</summary>
public sealed class CareerRequirement
{
    public string Text { get; set; } = string.Empty;
    public List<string> Alternatives { get; set; } = new();
}

/// <summary>Career-page requirement list; stored as JSON in a single system-setting row.</summary>
public sealed class CareerRequirementsConfig
{
    /// <summary>Upper bounds so a hand-edited row cannot blow up the public page.</summary>
    public const int MaxItems = 30;
    public const int MaxAlternatives = 6;
    public const int MaxTextLength = 500;

    public List<CareerRequirement> Items { get; set; } = new();

    public static CareerRequirementsConfig Default() => new()
    {
        Items =
        [
            new CareerRequirement
            {
                Text = "Mindestens 3 Monate durchgängige Zugehörigkeit beim Los Santos Police Department "
                       + "oder beim Department of Justice mit Erfahrung "
                       + "in einer Position mit Führungsverantwortung",
                Alternatives =
                [
                    "mindestens 6 Monate Zugehörigkeit bei der Military Police der Nationalgarde",
                    "mindestens 3 Monate Mitglied im Offiziersstab der Nationalgarde",
                ],
            },
            new CareerRequirement { Text = "Mindestens Visum Stufe 100" },
            new CareerRequirement { Text = "Nachweisbare geistige Reife sowie ein professionelles Auftreten" },
            new CareerRequirement { Text = "Keine schweren Vorstrafen innerhalb der letzten 12 Monate (Prüfung im Einzelfall)" },
            new CareerRequirement { Text = "Keine psychischen Erkrankungen (im RP)" },
            new CareerRequirement { Text = "Besitz einer Anwaltslizenz" },
            new CareerRequirement { Text = "Einschlägige Rechtskenntnisse" },
        ],
    };
}
