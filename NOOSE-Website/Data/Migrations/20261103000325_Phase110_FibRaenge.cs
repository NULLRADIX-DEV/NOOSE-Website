using Microsoft.EntityFrameworkCore.Migrations;
using NOOSE_Website.Infrastructure;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase110_FibRaenge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // one transaction, data only: all or nothing

            // 1. every stored rank +2, which keeps the order
            foreach (var (table, columns) in FibCutover.RankColumns)
            {
                var set = string.Join(", ", columns.Select(c => $"`{c}` = IF(`{c}` IN (5, 6), `{c}` + 2, `{c}`)"));
                var where = string.Join(" OR ", columns.Select(c => $"`{c}` IN (5, 6)"));
                migrationBuilder.Sql($"UPDATE `{table}` SET {set} WHERE {where}");
            }

            // 2. rank-keyed JSON, higher key first, partner agencies untouched
            foreach (var setting in new[] { "FinanzierungsBudgets", "KiKontingente" })
            {
                migrationBuilder.Sql(MoveRankKey(setting, 6, 8));
                migrationBuilder.Sql(MoveRankKey(setting, 5, 7));
            }
            foreach (var (rank, baseMonthly, carryOver) in FibCutover.FinancingStarters)
            {
                migrationBuilder.Sql(AddRankKey("FinanzierungsBudgets", rank,
                    $"JSON_OBJECT('BaseMonthly', {baseMonthly}, 'CarryOverPercent', {carryOver})"));
            }
            foreach (var (rank, baseWeekly, carryOver, daily) in FibCutover.QuotaStarters)
            {
                migrationBuilder.Sql(AddRankKey("KiKontingente", rank,
                    $"JSON_OBJECT('BaseWeekly', {baseWeekly}, 'CarryOverPercent', {carryOver}, 'DailyPercent', {daily})"));
            }

            // 3. counter-intelligence rules that name ranks; a 7 or 8 means it already ran
            migrationBuilder.Sql(
                "UPDATE `GegenaufklaerungsRegeln` SET `DefinitionJson` = JSON_SET(`DefinitionJson`, '$.ActorRanks', " +
                "JSON_EXTRACT(REPLACE(REPLACE(JSON_EXTRACT(`DefinitionJson`, '$.ActorRanks'), '6', '8'), '5', '7'), '$')) " +
                "WHERE JSON_VALID(`DefinitionJson`) " +
                "AND JSON_TYPE(JSON_EXTRACT(`DefinitionJson`, '$.ActorRanks')) = 'ARRAY' " +
                "AND (JSON_CONTAINS(`DefinitionJson`, '5', '$.ActorRanks') OR JSON_CONTAINS(`DefinitionJson`, '6', '$.ActorRanks')) " +
                "AND NOT JSON_CONTAINS(`DefinitionJson`, '7', '$.ActorRanks') " +
                "AND NOT JSON_CONTAINS(`DefinitionJson`, '8', '$.ActorRanks')");

            // 4. division of ranked internal accounts: HRT for the former TRU from Special Agent up, else CID
            migrationBuilder.Sql(
                "UPDATE `AspNetUsers` SET `Abteilung` = IF(`IstTRU` = 1 AND `Dienstgrad` >= 2, 3, 1) " +
                "WHERE `Partnerbehoerde` IS NULL AND `Dienstgrad` IS NOT NULL AND `Abteilung` IS NULL");

            // 5. TRU flag follows the division; never for a row without one, the column is NOT NULL
            migrationBuilder.Sql(
                "UPDATE `AspNetUsers` SET `IstTRU` = IF(`Abteilung` = 3 AND `Dienstgrad` IS NOT NULL AND `Dienstgrad` < 6, 1, 0) " +
                "WHERE `Abteilung` IS NOT NULL");

            // 6. old colours and logo give way to the code defaults
            migrationBuilder.Sql(
                $"DELETE FROM `SystemEinstellungen` WHERE `Schluessel` IN ({InList(FibCutover.RetiredSettingKeys)})");

            // 7. stored rank labels were keyed by the old names
            migrationBuilder.Sql("DELETE FROM `WertelistenLabels` WHERE `Liste` = 'Rank'");

            // 8. the seeder rewrites untouched handbook rows; deleted ones stay deleted
            migrationBuilder.Sql(
                $"UPDATE `HandbuchArtikel` SET `IstAngepasst` = 0 WHERE `SeedSchluessel` LIKE '{FibCutover.RewrittenArticleKeyPattern}' AND `IstGeloescht` = 0");
            migrationBuilder.Sql(
                $"UPDATE `HandbuchKapitel` SET `IstAngepasst` = 0 WHERE `SeedSchluessel` IN ({InList(FibCutover.RewrittenChapterKeys)}) AND `IstGeloescht` = 0");
            migrationBuilder.Sql(
                $"UPDATE `HandbuchBegriffe` SET `IstAngepasst` = 0 WHERE `SeedSchluessel` IN ({InList(FibCutover.RewrittenGlossaryKeys)})");

            // 9. everyone with a rank signs in again for fresh claims
            migrationBuilder.Sql("UPDATE `AspNetUsers` SET `SecurityStamp` = UUID() WHERE `Dienstgrad` IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to restore, going back means the pre-deploy dump
        }

        /// <summary>Moves one rank key of a settings blob up; skipped when the target key exists or the blob is not JSON.</summary>
        private static string MoveRankKey(string setting, int oldKey, int newKey) =>
            $"UPDATE `SystemEinstellungen` SET `Wert` = JSON_SET(JSON_REMOVE(`Wert`, '$.Ranks.\"{oldKey}\"'), '$.Ranks.\"{newKey}\"', JSON_EXTRACT(`Wert`, '$.Ranks.\"{oldKey}\"')) " +
            $"WHERE `Schluessel` = '{setting}' AND JSON_VALID(`Wert`) " +
            $"AND JSON_EXISTS(`Wert`, '$.Ranks.\"{oldKey}\"') AND NOT JSON_EXISTS(`Wert`, '$.Ranks.\"{newKey}\"')";

        /// <summary>Adds the starter entry of a new rank where the blob has a Ranks object and no entry for it yet.</summary>
        private static string AddRankKey(string setting, int rank, string entry) =>
            $"UPDATE `SystemEinstellungen` SET `Wert` = JSON_INSERT(`Wert`, '$.Ranks.\"{rank}\"', {entry}) " +
            $"WHERE `Schluessel` = '{setting}' AND JSON_VALID(`Wert`) " +
            $"AND JSON_EXISTS(`Wert`, '$.Ranks') AND NOT JSON_EXISTS(`Wert`, '$.Ranks.\"{rank}\"')";

        private static string InList(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => $"'{k}'"));
    }
}
