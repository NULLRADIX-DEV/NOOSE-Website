using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NOOSE_Website.Infrastructure;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase124_Dienstverordnung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DienstanweisungFassungen");

            migrationBuilder.DropTable(
                name: "DienstanweisungKenntnisnahmen");

            migrationBuilder.DropTable(
                name: "Dienstanweisungen");

            migrationBuilder.CreateTable(
                name: "DvoAbschnitte",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    IstVorwort = table.Column<bool>(type: "tinyint(1)", nullable: false),
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
                    table.PrimaryKey("PK_DvoAbschnitte", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DvoAbsaetze",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AbschnittId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    Zielgruppe = table.Column<int>(type: "int", nullable: false),
                    AktuelleFassung = table.Column<int>(type: "int", nullable: false),
                    KenntnisnahmeAbFassung = table.Column<int>(type: "int", nullable: false),
                    AufgehobenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AufgehobenVonId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AufgehobenVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Aufhebungsgrund = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
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
                    table.PrimaryKey("PK_DvoAbsaetze", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DvoAbsaetze_DvoAbschnitte_AbschnittId",
                        column: x => x.AbschnittId,
                        principalTable: "DvoAbschnitte",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DvoAbsatzFassungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AbsatzId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Fassung = table.Column<int>(type: "int", nullable: false),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Zielgruppe = table.Column<int>(type: "int", nullable: false),
                    InhaltHtml = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GueltigAb = table.Column<DateOnly>(type: "date", nullable: false),
                    Aenderungsnotiz = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KenntnisnahmeNoetig = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    VeroeffentlichtVonId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VeroeffentlichtVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VeroeffentlichtAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DvoAbsatzFassungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DvoAbsatzFassungen_DvoAbsaetze_AbsatzId",
                        column: x => x.AbsatzId,
                        principalTable: "DvoAbsaetze",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DvoKenntnisnahmen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AbsatzId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Fassung = table.Column<int>(type: "int", nullable: false),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KenntnisGenommenAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DvoKenntnisnahmen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DvoKenntnisnahmen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DvoKenntnisnahmen_DvoAbsaetze_AbsatzId",
                        column: x => x.AbsatzId,
                        principalTable: "DvoAbsaetze",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_DvoAbsaetze_AbschnittId_Reihenfolge",
                table: "DvoAbsaetze",
                columns: new[] { "AbschnittId", "Reihenfolge" });

            migrationBuilder.CreateIndex(
                name: "IX_DvoAbsaetze_AufgehobenAm",
                table: "DvoAbsaetze",
                column: "AufgehobenAm");

            migrationBuilder.CreateIndex(
                name: "IX_DvoAbsatzFassungen_AbsatzId_Fassung",
                table: "DvoAbsatzFassungen",
                columns: new[] { "AbsatzId", "Fassung" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DvoKenntnisnahmen_AbsatzId_AgentId_Fassung",
                table: "DvoKenntnisnahmen",
                columns: new[] { "AbsatzId", "AgentId", "Fassung" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DvoKenntnisnahmen_AgentId",
                table: "DvoKenntnisnahmen",
                column: "AgentId");

            // the handbook stops retelling the regulation; the seeder never deletes, so hide by seed key
            migrationBuilder.Sql(RegulationCutover.HideSql("HandbuchArtikel", RegulationCutover.RemovedArticleKeys));
            migrationBuilder.Sql(RegulationCutover.HideSql("HandbuchKapitel", RegulationCutover.RemovedChapterKeys));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DvoAbsatzFassungen");

            migrationBuilder.DropTable(
                name: "DvoKenntnisnahmen");

            migrationBuilder.DropTable(
                name: "DvoAbsaetze");

            migrationBuilder.DropTable(
                name: "DvoAbschnitte");

            migrationBuilder.CreateTable(
                name: "Dienstanweisungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KenntnisnahmeAbFassung = table.Column<int>(type: "int", nullable: false),
                    Einstufung = table.Column<int>(type: "int", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AktuelleFassung = table.Column<int>(type: "int", nullable: false),
                    GeloeschtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeloeschtVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IstGeloescht = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Nummer = table.Column<int>(type: "int", nullable: false),
                    Aufhebungsgrund = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AufgehobenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AufgehobenVonId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AufgehobenVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sachgebiet = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Dienstanweisungen", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DienstanweisungFassungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DienstanweisungId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Aenderungsnotiz = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InhaltHtml = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VeroeffentlichtAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    VeroeffentlichtVonId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VeroeffentlichtVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KenntnisnahmeNoetig = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GueltigAb = table.Column<DateOnly>(type: "date", nullable: false),
                    Fassung = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DienstanweisungFassungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DienstanweisungFassungen_Dienstanweisungen_DienstanweisungId",
                        column: x => x.DienstanweisungId,
                        principalTable: "Dienstanweisungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DienstanweisungKenntnisnahmen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DienstanweisungId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    KenntnisGenommenAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Fassung = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DienstanweisungKenntnisnahmen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DienstanweisungKenntnisnahmen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DienstanweisungKenntnisnahmen_Dienstanweisungen_Dienstanweis~",
                        column: x => x.DienstanweisungId,
                        principalTable: "Dienstanweisungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Dienstanweisungen_AufgehobenAm",
                table: "Dienstanweisungen",
                column: "AufgehobenAm");

            migrationBuilder.CreateIndex(
                name: "IX_Dienstanweisungen_Nummer",
                table: "Dienstanweisungen",
                column: "Nummer",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DienstanweisungFassungen_DienstanweisungId_Fassung",
                table: "DienstanweisungFassungen",
                columns: new[] { "DienstanweisungId", "Fassung" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DienstanweisungKenntnisnahmen_AgentId",
                table: "DienstanweisungKenntnisnahmen",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_DienstanweisungKenntnisnahmen_DienstanweisungId_AgentId_Fass~",
                table: "DienstanweisungKenntnisnahmen",
                columns: new[] { "DienstanweisungId", "AgentId", "Fassung" },
                unique: true);
        }
    }
}
