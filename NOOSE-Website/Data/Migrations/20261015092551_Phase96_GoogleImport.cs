using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase96_GoogleImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImportHtml",
                table: "Dokumente",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "ImportQuelle",
                table: "Dokumente",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportQuelleId",
                table: "Dokumente",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ImportiertAm",
                table: "Dokumente",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImportiertVonId",
                table: "Dokumente",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImportHtml",
                table: "Dokumente");

            migrationBuilder.DropColumn(
                name: "ImportQuelle",
                table: "Dokumente");

            migrationBuilder.DropColumn(
                name: "ImportQuelleId",
                table: "Dokumente");

            migrationBuilder.DropColumn(
                name: "ImportiertAm",
                table: "Dokumente");

            migrationBuilder.DropColumn(
                name: "ImportiertVonId",
                table: "Dokumente");
        }
    }
}
