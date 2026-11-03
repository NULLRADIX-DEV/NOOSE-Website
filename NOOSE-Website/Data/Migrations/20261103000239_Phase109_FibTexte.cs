using Microsoft.EntityFrameworkCore.Migrations;
using NOOSE_Website.Infrastructure;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase109_FibTexte : Migration
    {
        /// <summary>Copies of the rows the rank migration resets, taken before the sweep; a re-run keeps the first copy.</summary>
        public static IReadOnlyList<string> BackupStatements { get; } =
        [
            "CREATE TABLE IF NOT EXISTS `HandbuchArtikel_Fib_Sicherung` AS SELECT * FROM `HandbuchArtikel` " +
            $"WHERE `SeedSchluessel` LIKE '{FibCutover.RewrittenArticleKeyPattern}'",

            "CREATE TABLE IF NOT EXISTS `HandbuchKapitel_Fib_Sicherung` AS SELECT * FROM `HandbuchKapitel` " +
            $"WHERE `SeedSchluessel` IN ({InList(FibCutover.RewrittenChapterKeys)})",

            "CREATE TABLE IF NOT EXISTS `HandbuchSchritte_Fib_Sicherung` AS SELECT s.* FROM `HandbuchSchritte` s " +
            "INNER JOIN `HandbuchArtikel` a ON a.`Id` = s.`ArtikelId` " +
            $"WHERE a.`SeedSchluessel` LIKE '{FibCutover.RewrittenArticleKeyPattern}'",

            "CREATE TABLE IF NOT EXISTS `HandbuchBegriffe_Fib_Sicherung` AS SELECT * FROM `HandbuchBegriffe` " +
            $"WHERE `SeedSchluessel` IN ({InList(FibCutover.RewrittenGlossaryKeys)})",

            "CREATE TABLE IF NOT EXISTS `WertelistenLabels_Fib_Sicherung` AS SELECT * FROM `WertelistenLabels` " +
            "WHERE `Liste` = 'Rank'",
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // text only and one command each, so a re-run just finds less to rewrite
            foreach (var sql in BackupStatements)
            {
                migrationBuilder.Sql(sql, suppressTransaction: true);
            }

            // columns from the frozen model, swaps from BrandSweep
            var plan = BrandSweepPlan.Classify(TargetModel);
            foreach (var sql in BrandSweepPlan.Statements(plan))
            {
                migrationBuilder.Sql(sql, suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to restore, the old wording is in the pre-deploy dump
        }

        private static string InList(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => $"'{k}'"));
    }
}
