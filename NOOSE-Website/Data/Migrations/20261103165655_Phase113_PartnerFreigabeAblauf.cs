using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase113_PartnerFreigabeAblauf : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "GueltigBis",
                table: "PartnerFreigaben",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "FreigabeGueltigBis",
                table: "Antraege",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "GueltigBis",
                table: "PartnerFreigaben");

            migrationBuilder.DropColumn(
                name: "FreigabeGueltigBis",
                table: "Antraege");
        }
    }
}
