using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase97_Praesentationen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Praesentationen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sichtbarkeit = table.Column<int>(type: "int", nullable: false),
                    Zielgruppe = table.Column<int>(type: "int", nullable: true),
                    ZielId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MindestDienstgrad = table.Column<int>(type: "int", nullable: true),
                    BesitzerId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IstGeloescht = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GeloeschtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeloeschtVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Praesentationen", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PraesentationEinladungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PresentationId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
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
                    table.PrimaryKey("PK_PraesentationEinladungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PraesentationEinladungen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PraesentationEinladungen_Praesentationen_PresentationId",
                        column: x => x.PresentationId,
                        principalTable: "Praesentationen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PraesentationFolien",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PresentationId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sortierung = table.Column<int>(type: "int", nullable: false),
                    Layout = table.Column<int>(type: "int", nullable: false),
                    Titel = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InhaltHtml = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BildId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Notizen = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IstGeloescht = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GeloeschtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeloeschtVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PraesentationFolien", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PraesentationFolien_Praesentationen_PresentationId",
                        column: x => x.PresentationId,
                        principalTable: "Praesentationen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PraesentationEinladungen_AgentId",
                table: "PraesentationEinladungen",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_PraesentationEinladungen_PresentationId_AgentId",
                table: "PraesentationEinladungen",
                columns: new[] { "PresentationId", "AgentId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Praesentationen_BesitzerId",
                table: "Praesentationen",
                column: "BesitzerId");

            migrationBuilder.CreateIndex(
                name: "IX_Praesentationen_Sichtbarkeit",
                table: "Praesentationen",
                column: "Sichtbarkeit");

            migrationBuilder.CreateIndex(
                name: "IX_PraesentationFolien_PresentationId_Sortierung",
                table: "PraesentationFolien",
                columns: new[] { "PresentationId", "Sortierung" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PraesentationEinladungen");

            migrationBuilder.DropTable(
                name: "PraesentationFolien");

            migrationBuilder.DropTable(
                name: "Praesentationen");
        }
    }
}
