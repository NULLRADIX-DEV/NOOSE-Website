namespace NOOSE_Website.Infrastructure;

/// <summary>Fixed lists of the NOOSE to FIB data migrations, held against the model by a test.</summary>
public static class FibCutover
{
    /// <summary>Columns that store a <c>Rank</c>; the old 5 and 6 move up by two.</summary>
    public static IReadOnlyList<(string Table, string[] Columns)> RankColumns { get; } =
    [
        ("AspNetUsers", ["Dienstgrad"]),
        ("AgentDienstgradVerlaeufe", ["Alt", "Neu"]),
        ("AgentBefoerderungsantraege", ["ZielDienstgrad"]),
        ("Ehrungen", ["Dienstgrad", "UnterzeichnerDienstgrad"]),
        ("Ankuendigungen", ["MinDienstgrad"]),
        ("Praesentationen", ["MindestDienstgrad"]),
        ("Finanzierungspositionen", ["MindestDienstgrad"]),
        ("Finanzierungsbudgetperioden", ["DienstgradBeiAbschluss"]),
        ("KiKontingentperioden", ["DienstgradBeiAbschluss"]),
    ];

    /// <summary>Starter budget of the two new ranks, same as <c>FinancingBudgetConfig.Default()</c>.</summary>
    public static IReadOnlyList<(int Rank, int BaseMonthly, int CarryOverPercent)> FinancingStarters { get; } =
        [(5, 165_000, 50), (6, 180_000, 50)];

    /// <summary>Starter AI quota of the two new ranks, same as <c>LlmQuotaConfig.Default()</c>.</summary>
    public static IReadOnlyList<(int Rank, int BaseWeekly, int CarryOverPercent, int DailyPercent)> QuotaStarters { get; } =
        [(5, 95_000, 50, 40), (6, 105_000, 50, 40)];

    /// <summary>Seed key of the handbook articles the new text replaces.</summary>
    public const string RewrittenArticleKeyPattern = "art-dvo-%";

    /// <summary>Seed keys of the two handbook chapters the new text replaces.</summary>
    public static IReadOnlyList<string> RewrittenChapterKeys { get; } = ["kap-dienstvorschrift", "kap-ausruestung"];

    /// <summary>Seed keys of the glossary terms the new wording replaces.</summary>
    public static IReadOnlyList<string> RewrittenGlossaryKeys { get; } =
    [
        "beg-dienstgrad", "beg-fuehrung", "beg-tru", "beg-hrb", "beg-kennzeichen", "beg-junior-agent",
        "beg-special-agent", "beg-senior-special-agent", "beg-supervisory", "beg-deputy-director", "beg-director",
        "beg-vs", "beg-vs-stufe", "beg-gefahrenlage", "beg-befoerderung", "beg-organigramm", "beg-handbuch",
        "beg-confidential", "beg-secret", "beg-top-secret", "beg-cid", "beg-undercover",
    ];

    /// <summary>Settings overrides of the old look; the secondary colour stays.</summary>
    public static IReadOnlyList<string> RetiredSettingKeys { get; } = ["ThemePrimary", "ThemeTertiary", "LogoDateiname", "LogoContentType"];
}
