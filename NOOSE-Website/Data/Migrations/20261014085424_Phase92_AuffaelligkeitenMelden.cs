using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase92_AuffaelligkeitenMelden : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "MeldenAb",
                table: "GegenaufklaerungsRegeln",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "Meldet",
                table: "GegenaufklaerungsRegeln",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "AuffaelligkeitMeldungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Quelle = table.Column<int>(type: "int", nullable: false),
                    Schluessel = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GemeldetBis = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Stufe = table.Column<int>(type: "int", nullable: false),
                    GemeldetAm = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuffaelligkeitMeldungen", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AuffaelligkeitMeldungen_Quelle_GemeldetAm",
                table: "AuffaelligkeitMeldungen",
                columns: new[] { "Quelle", "GemeldetAm" });

            migrationBuilder.CreateIndex(
                name: "IX_AuffaelligkeitMeldungen_Quelle_Schluessel_AgentId",
                table: "AuffaelligkeitMeldungen",
                columns: new[] { "Quelle", "Schluessel", "AgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AuffaelligkeitMeldungen");

            migrationBuilder.DropColumn(
                name: "MeldenAb",
                table: "GegenaufklaerungsRegeln");

            migrationBuilder.DropColumn(
                name: "Meldet",
                table: "GegenaufklaerungsRegeln");
        }
    }
}
