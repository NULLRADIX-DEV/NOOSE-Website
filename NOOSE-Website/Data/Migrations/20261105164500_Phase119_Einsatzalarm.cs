using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase119_Einsatzalarm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Einsatzalarme",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Lage = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Ort = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Ziel = table.Column<int>(type: "int", nullable: true),
                    GefahrImVerzug = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Begruendung = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AusgeloestVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AusgeloestVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AusgeloestAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Ablauf = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    BeendetAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BeendetVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    NachgepingtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    OperationId = table.Column<string>(type: "varchar(255)", nullable: true)
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
                    table.PrimaryKey("PK_Einsatzalarme", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Einsatzalarme_Operationen_OperationId",
                        column: x => x.OperationId,
                        principalTable: "Operationen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "EinsatzalarmEmpfaenger",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AlarmId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Antwort = table.Column<int>(type: "int", nullable: true),
                    GeantwortetAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EinsatzalarmEmpfaenger", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EinsatzalarmEmpfaenger_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_EinsatzalarmEmpfaenger_Einsatzalarme_AlarmId",
                        column: x => x.AlarmId,
                        principalTable: "Einsatzalarme",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Einsatzalarme_Ablauf_BeendetAm",
                table: "Einsatzalarme",
                columns: new[] { "Ablauf", "BeendetAm" });

            migrationBuilder.CreateIndex(
                name: "IX_Einsatzalarme_OperationId",
                table: "Einsatzalarme",
                column: "OperationId");

            migrationBuilder.CreateIndex(
                name: "IX_EinsatzalarmEmpfaenger_AgentId",
                table: "EinsatzalarmEmpfaenger",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_EinsatzalarmEmpfaenger_AlarmId_AgentId",
                table: "EinsatzalarmEmpfaenger",
                columns: new[] { "AlarmId", "AgentId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EinsatzalarmEmpfaenger");

            migrationBuilder.DropTable(
                name: "Einsatzalarme");
        }
    }
}
