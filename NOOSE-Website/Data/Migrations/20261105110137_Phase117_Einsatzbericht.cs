using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase117_Einsatzbericht : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // As varchar the row broke the 65,535-byte limit and the first fib deploy stopped halfway, after two of
            // the three columns. IF NOT EXISTS picks up from there, MODIFY moves anything already there to longtext.
            migrationBuilder.Sql("""
                ALTER TABLE `Operationen`
                    ADD COLUMN IF NOT EXISTS `BeteiligteBehoerden` varchar(1000) CHARACTER SET utf8mb4 NULL,
                    ADD COLUMN IF NOT EXISTS `Beweismittel` longtext CHARACTER SET utf8mb4 NULL,
                    ADD COLUMN IF NOT EXISTS `Legitimation` longtext CHARACTER SET utf8mb4 NULL;
                """);
            migrationBuilder.Sql("""
                ALTER TABLE `Operationen`
                    MODIFY COLUMN `Beweismittel` longtext CHARACTER SET utf8mb4 NULL,
                    MODIFY COLUMN `Legitimation` longtext CHARACTER SET utf8mb4 NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BeteiligteBehoerden",
                table: "Operationen");

            migrationBuilder.DropColumn(
                name: "Beweismittel",
                table: "Operationen");

            migrationBuilder.DropColumn(
                name: "Legitimation",
                table: "Operationen");
        }
    }
}
