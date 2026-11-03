using Microsoft.EntityFrameworkCore.Migrations;
using NOOSE_Website.Infrastructure;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase109_FibTexte : Migration
    {
        /// <summary>Copies of the rows Phase110 resets, taken before the sweep touches their wording.</summary>
        /// <remarks><c>CREATE TABLE IF NOT EXISTS</c>: a re-run keeps the first copy instead of failing or saving the swept text.</remarks>
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
            // Text only, so a failure half-way leaves nothing the previous release cannot read, and a re-run simply
            // finds less to rewrite. Every command stands alone (suppressTransaction): the backup tables are DDL, and
            // one UPDATE per table is its own unit with its own timeout (Program.cs raises it for the migrator).
            foreach (var sql in BackupStatements)
            {
                migrationBuilder.Sql(sql, suppressTransaction: true);
            }

            // The columns come from this migration's own frozen model, the swaps from BrandSweep.Replacements
            // (the same table the demo literals are checked against); no PREPARE, no user variables.
            var plan = BrandSweepPlan.Classify(TargetModel);
            foreach (var sql in BrandSweepPlan.Statements(plan))
            {
                migrationBuilder.Sql(sql, suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to restore: the old wording is only in the pre-deploy dump (and the *_Fib_Sicherung tables for the handbook)
        }

        private static string InList(IEnumerable<string> keys) => string.Join(", ", keys.Select(k => $"'{k}'"));
    }
}
