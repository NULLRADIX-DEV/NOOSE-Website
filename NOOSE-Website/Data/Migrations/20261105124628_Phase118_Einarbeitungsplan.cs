using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase118_Einarbeitungsplan : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IstPflicht",
                table: "AusbildungsModule",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "AgentEinarbeitungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PateId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    BeginntAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    EndetAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErinnerungsStufe = table.Column<int>(type: "int", nullable: false),
                    AbgeschlossenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Ergebnis = table.Column<int>(type: "int", nullable: true),
                    AbgeschlossenVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
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
                    table.PrimaryKey("PK_AgentEinarbeitungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentEinarbeitungen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentEinarbeitungen_AspNetUsers_PateId",
                        column: x => x.PateId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEinarbeitungen_AbgeschlossenAm_EndetAm",
                table: "AgentEinarbeitungen",
                columns: new[] { "AbgeschlossenAm", "EndetAm" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentEinarbeitungen_AgentId",
                table: "AgentEinarbeitungen",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentEinarbeitungen_PateId",
                table: "AgentEinarbeitungen",
                column: "PateId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentEinarbeitungen");

            migrationBuilder.DropColumn(
                name: "IstPflicht",
                table: "AusbildungsModule");
        }
    }
}
