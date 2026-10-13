using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase90_QuittierungFrist : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "QuittierenBis",
                table: "Ankuendigungen",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "QuittierungErinnertAm",
                table: "Ankuendigungen",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuittierenBis",
                table: "Ankuendigungen");

            migrationBuilder.DropColumn(
                name: "QuittierungErinnertAm",
                table: "Ankuendigungen");
        }
    }
}
