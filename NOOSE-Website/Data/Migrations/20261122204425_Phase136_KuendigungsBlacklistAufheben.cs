using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <summary>
    /// Lifts the blacklist entries a termination used to set on its own (reason "Kündigung: …") before 3.0.67.
    /// Soft-deleted like a manual lift, so a row stays recoverable; hand-set blacklists are untouched.
    /// </summary>
    public partial class Phase136_KuendigungsBlacklistAufheben : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "UPDATE `Bewerbungssperren` SET `IstGeloescht` = 1, `GeloeschtAm` = UTC_TIMESTAMP() "
                + "WHERE `IstBlacklist` = 1 AND `IstGeloescht` = 0 AND `Grund` LIKE 'Kündigung: %';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to restore: the lifted rows can no longer be told apart from ones lifted by hand
        }
    }
}
