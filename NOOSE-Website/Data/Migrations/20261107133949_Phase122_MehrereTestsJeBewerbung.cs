using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase122_MehrereTestsJeBewerbung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // new index first: MariaDB will not drop the one the foreign key on BewerbungId still needs
            migrationBuilder.CreateIndex(
                name: "IX_BewerbungTestZuweisungen_BewerbungId_TestId",
                table: "BewerbungTestZuweisungen",
                columns: new[] { "BewerbungId", "TestId" },
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_BewerbungTestZuweisungen_BewerbungId",
                table: "BewerbungTestZuweisungen");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_BewerbungTestZuweisungen_BewerbungId",
                table: "BewerbungTestZuweisungen",
                column: "BewerbungId",
                unique: true);

            migrationBuilder.DropIndex(
                name: "IX_BewerbungTestZuweisungen_BewerbungId_TestId",
                table: "BewerbungTestZuweisungen");
        }
    }
}
