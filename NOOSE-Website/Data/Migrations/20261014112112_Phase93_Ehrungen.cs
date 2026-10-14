using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase93_Ehrungen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EhrenzeichenJson",
                table: "OeffentlicheFuehrungsprofile",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Ehrenzeichen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(80)", maxLength: 80, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Standardtext = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Symbol = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Stufe = table.Column<int>(type: "int", nullable: false),
                    Aktiv = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Ehrenzeichen", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Ehrungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EhrenzeichenId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerliehenAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Verleihungstext = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Dienstgrad = table.Column<int>(type: "int", nullable: true),
                    VerliehenVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VerliehenVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UnterzeichnerName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UnterzeichnerDienstgrad = table.Column<int>(type: "int", nullable: true),
                    AnkuendigungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AberkanntAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AberkanntVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AberkennungsGrund = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_Ehrungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ehrungen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Ehrungen_Ehrenzeichen_EhrenzeichenId",
                        column: x => x.EhrenzeichenId,
                        principalTable: "Ehrenzeichen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Ehrenzeichen_Name",
                table: "Ehrenzeichen",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ehrungen_AgentId_AberkanntAm",
                table: "Ehrungen",
                columns: new[] { "AgentId", "AberkanntAm" });

            migrationBuilder.CreateIndex(
                name: "IX_Ehrungen_EhrenzeichenId",
                table: "Ehrungen",
                column: "EhrenzeichenId");

            migrationBuilder.CreateIndex(
                name: "IX_Ehrungen_VerliehenAm",
                table: "Ehrungen",
                column: "VerliehenAm");

            // starter catalog; leadership renames, switches off or deletes them like any other entry
            migrationBuilder.InsertData(
                table: "Ehrenzeichen",
                columns: new[] { "Id", "Name", "Beschreibung", "Standardtext", "Symbol", "Stufe", "Aktiv", "Reihenfolge", "ErstelltAm" },
                values: new object[,]
                {
                    {
                        "22222222-e4e2-4a01-9000-000000000001",
                        "Verdienstmedaille",
                        "Für herausragende Verdienste um die NOOSE.",
                        "In Anerkennung herausragender Verdienste um das National Office of Security Enforcement.",
                        "medaille", 0, true, 10,
                        new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)
                    },
                    {
                        "22222222-e4e2-4a01-9000-000000000002",
                        "Tapferkeitsmedaille",
                        "Für mutiges Handeln im Einsatz unter Gefahr.",
                        "Für mutiges und entschlossenes Handeln im Einsatz unter Gefahr für Leib und Leben.",
                        "stern", 1, true, 20,
                        new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)
                    },
                    {
                        "22222222-e4e2-4a01-9000-000000000003",
                        "Ehrenkreuz",
                        "Für besondere Leistungen in Ermittlung oder Ausbildung.",
                        "Für besondere Leistungen in Ermittlung und Ausbildung, die über das Maß der Pflicht hinausgehen.",
                        "orden", 2, true, 30,
                        new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)
                    },
                    {
                        "22222222-e4e2-4a01-9000-000000000004",
                        "Dienstjubiläum",
                        "Für langjährige treue Dienste.",
                        "Für langjährige treue Dienste im National Office of Security Enforcement.",
                        "jubilaeum", 3, true, 40,
                        new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc)
                    },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Ehrungen");

            migrationBuilder.DropTable(
                name: "Ehrenzeichen");

            migrationBuilder.DropColumn(
                name: "EhrenzeichenJson",
                table: "OeffentlicheFuehrungsprofile");
        }
    }
}
