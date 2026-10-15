using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase98_GoogleSlidesImport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PdfDateiId",
                table: "PraesentationFolien",
                type: "varchar(255)",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Seite",
                table: "PraesentationFolien",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PraesentationDateien",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PresentationId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateiName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Groesse = table.Column<long>(type: "bigint", nullable: false),
                    Seiten = table.Column<int>(type: "int", nullable: false),
                    Revision = table.Column<int>(type: "int", nullable: false),
                    ImportQuelle = table.Column<int>(type: "int", nullable: false),
                    ImportQuelleId = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ImportiertAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ImportiertVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PraesentationDateien", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PraesentationDateien_Praesentationen_PresentationId",
                        column: x => x.PresentationId,
                        principalTable: "Praesentationen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PraesentationFolien_PdfDateiId",
                table: "PraesentationFolien",
                column: "PdfDateiId");

            migrationBuilder.CreateIndex(
                name: "IX_PraesentationDateien_PresentationId",
                table: "PraesentationDateien",
                column: "PresentationId");

            migrationBuilder.AddForeignKey(
                name: "FK_PraesentationFolien_PraesentationDateien_PdfDateiId",
                table: "PraesentationFolien",
                column: "PdfDateiId",
                principalTable: "PraesentationDateien",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PraesentationFolien_PraesentationDateien_PdfDateiId",
                table: "PraesentationFolien");

            migrationBuilder.DropTable(
                name: "PraesentationDateien");

            migrationBuilder.DropIndex(
                name: "IX_PraesentationFolien_PdfDateiId",
                table: "PraesentationFolien");

            migrationBuilder.DropColumn(
                name: "PdfDateiId",
                table: "PraesentationFolien");

            migrationBuilder.DropColumn(
                name: "Seite",
                table: "PraesentationFolien");
        }
    }
}
