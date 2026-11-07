namespace NOOSE_Website.Infrastructure;

/// <summary>
/// Handbook rows that retold the service regulation and are gone since the regulation has its own page (Phase124).
/// </summary>
/// <remarks>
/// The seeder never deletes a row, so the migration moves these to the trash by seed key and frees their slugs:
/// <c>art-dienstverordnung</c> ships with the slug the old summary article held, and the unique slug index would make
/// the seeder skip it silently. An explicit list, never <see cref="FibCutover.RewrittenArticleKeyPattern"/>: three
/// <c>art-dvo-*</c> articles stay.
/// </remarks>
public static class RegulationCutover
{
    public static readonly IReadOnlyList<string> RemovedArticleKeys =
    [
        "art-dvo-dienstverordnung", "art-dvo-dienstregeln", "art-dvo-freigaben", "art-dvo-aufgaben",
        "art-dvo-ausbildungen", "art-dvo-befoerderung", "art-dvo-verhalten", "art-dvo-weisung", "art-dvo-sanktionen",
        "art-dvo-regeldienst", "art-dvo-undercover", "art-dvo-zusammenarbeit", "art-dvo-ausruestung", "art-dvo-gehalt",
        "art-dvo-funk", "art-dvo-fahrzeuge", "art-dvo-gebaeude", "art-dvo-dienstmittel", "art-dienstanweisungen",
    ];

    public static readonly IReadOnlyList<string> RemovedChapterKeys = ["kap-ausruestung"];

    /// <summary>Soft delete plus a freed slug; a row already in the trash only gets its slug freed.</summary>
    public static string HideSql(string table, IReadOnlyList<string> keys)
    {
        var list = string.Join(", ", keys.Select(k => $"'{k}'"));
        return $"UPDATE `{table}` SET "
               + "`GeloeschtAm` = CASE WHEN `IstGeloescht` = 0 THEN UTC_TIMESTAMP(6) ELSE `GeloeschtAm` END, "
               + "`IstGeloescht` = 1, "
               + "`Slug` = CONCAT(`Slug`, '-alt') "
               + $"WHERE `SeedSchluessel` IN ({list}) AND `Slug` NOT LIKE '%-alt'";
    }
}
