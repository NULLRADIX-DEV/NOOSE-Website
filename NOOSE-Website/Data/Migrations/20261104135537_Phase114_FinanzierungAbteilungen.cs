using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase114_FinanzierungAbteilungen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MindestDienstgradCtd",
                table: "Finanzierungspositionen",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MindestDienstgradHrt",
                table: "Finanzierungspositionen",
                type: "int",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MindestDienstgradCtd",
                table: "Finanzierungspositionen");

            migrationBuilder.DropColumn(
                name: "MindestDienstgradHrt",
                table: "Finanzierungspositionen");
        }
    }
}
