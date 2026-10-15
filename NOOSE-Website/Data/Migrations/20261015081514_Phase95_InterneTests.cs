using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase95_InterneTests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AusbildungsmodulId",
                table: "BewerbungTests",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Verwendung",
                table: "BewerbungTests",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AgentTestZuweisungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TestId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ZugewiesenVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ZugewiesenVon = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FaelligAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GestartetAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    FristBis = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BearbeitungszeitMinuten = table.Column<int>(type: "int", nullable: true),
                    ZusatzMinuten = table.Column<int>(type: "int", nullable: false),
                    ZeitAbgelaufen = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Versuch = table.Column<int>(type: "int", nullable: false, defaultValue: 1),
                    AbgeschlossenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BewertetAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    BewertetVon = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErgebnisPunkte = table.Column<int>(type: "int", nullable: true),
                    ErgebnisMaxPunkte = table.Column<int>(type: "int", nullable: true),
                    ErgebnisBestehensgrenze = table.Column<int>(type: "int", nullable: true),
                    Bestanden = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    PersonalakteVermerkId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FaelligErinnertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    UeberfaelligErinnertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_AgentTestZuweisungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTestZuweisungen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentTestZuweisungen_BewerbungTests_TestId",
                        column: x => x.TestId,
                        principalTable: "BewerbungTests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "AgentTestAntworten",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AssignmentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    QuestionId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OptionId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OptionIds = table.Column<string>(type: "varchar(2048)", maxLength: 2048, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Freitext = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ManuellRichtig = table.Column<bool>(type: "tinyint(1)", nullable: true),
                    ManuellPunkte = table.Column<int>(type: "int", nullable: true),
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
                    table.PrimaryKey("PK_AgentTestAntworten", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentTestAntworten_AgentTestZuweisungen_AssignmentId",
                        column: x => x.AssignmentId,
                        principalTable: "AgentTestZuweisungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentTestAntworten_BewerbungTestFragen_QuestionId",
                        column: x => x.QuestionId,
                        principalTable: "BewerbungTestFragen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestAntworten_AssignmentId_QuestionId",
                table: "AgentTestAntworten",
                columns: new[] { "AssignmentId", "QuestionId" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestAntworten_QuestionId",
                table: "AgentTestAntworten",
                column: "QuestionId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestZuweisungen_AbgeschlossenAm_FristBis",
                table: "AgentTestZuweisungen",
                columns: new[] { "AbgeschlossenAm", "FristBis" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestZuweisungen_AgentId_AbgeschlossenAm",
                table: "AgentTestZuweisungen",
                columns: new[] { "AgentId", "AbgeschlossenAm" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestZuweisungen_FaelligAm",
                table: "AgentTestZuweisungen",
                column: "FaelligAm");

            migrationBuilder.CreateIndex(
                name: "IX_AgentTestZuweisungen_TestId_AbgeschlossenAm",
                table: "AgentTestZuweisungen",
                columns: new[] { "TestId", "AbgeschlossenAm" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AgentTestAntworten");

            migrationBuilder.DropTable(
                name: "AgentTestZuweisungen");

            migrationBuilder.DropColumn(
                name: "AusbildungsmodulId",
                table: "BewerbungTests");

            migrationBuilder.DropColumn(
                name: "Verwendung",
                table: "BewerbungTests");
        }
    }
}
