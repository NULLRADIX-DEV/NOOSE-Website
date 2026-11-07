using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase123_Dienstanweisungen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Dienstanweisungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Nummer = table.Column<int>(type: "int", nullable: false),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Sachgebiet = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Einstufung = table.Column<int>(type: "int", nullable: false),
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
                    Fassung = table.Column<int>(type: "int", nullable: false),
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
                    DienstanweisungId = table.Column<string>(type: "varchar(255)", nullable: false)
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

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DienstanweisungFassungen");

            migrationBuilder.DropTable(
                name: "DienstanweisungKenntnisnahmen");

            migrationBuilder.DropTable(
                name: "Dienstanweisungen");
        }
    }
}
