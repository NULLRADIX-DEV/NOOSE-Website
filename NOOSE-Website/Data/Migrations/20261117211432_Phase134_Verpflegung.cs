using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase134_Verpflegung : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "VerpflegungslimitEssen",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "VerpflegungslimitTrinken",
                table: "AspNetUsers",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Verpflegungsanforderungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Aktenzeichen = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Begruendung = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EssenBeantragt = table.Column<int>(type: "int", nullable: false),
                    TrinkenBeantragt = table.Column<int>(type: "int", nullable: false),
                    EssenGenehmigt = table.Column<int>(type: "int", nullable: true),
                    TrinkenGenehmigt = table.Column<int>(type: "int", nullable: true),
                    LimitWoche = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    EntscheiderName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntschiedenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Entscheidungsnotiz = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    UeberschreitungEssen = table.Column<int>(type: "int", nullable: true),
                    UeberschreitungTrinken = table.Column<int>(type: "int", nullable: true),
                    UeberschreitungsBegruendung = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AusgegebenAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    AusgegebenVonName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AsservatEintragId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
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
                    table.PrimaryKey("PK_Verpflegungsanforderungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Verpflegungsanforderungen_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Verpflegungspositionen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AsservatId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Art = table.Column<int>(type: "int", nullable: false),
                    Beschreibung = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    MaxMenge = table.Column<int>(type: "int", nullable: false),
                    IstAktiv = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    Sortierung = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_Verpflegungspositionen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Verpflegungspositionen_Asservate_AsservatId",
                        column: x => x.AsservatId,
                        principalTable: "Asservate",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Verpflegungsanforderungspositionen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AnforderungId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PositionId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AsservatId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Bezeichnung = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Art = table.Column<int>(type: "int", nullable: false),
                    Menge = table.Column<int>(type: "int", nullable: false),
                    GenehmigteMenge = table.Column<int>(type: "int", nullable: true),
                    Sortierung = table.Column<int>(type: "int", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Verpflegungsanforderungspositionen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Verpflegungsanforderungspositionen_Verpflegungsanforderungen~",
                        column: x => x.AnforderungId,
                        principalTable: "Verpflegungsanforderungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Verpflegungsanforderungspositionen_Verpflegungspositionen_Po~",
                        column: x => x.PositionId,
                        principalTable: "Verpflegungspositionen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungen_AgentId_Status",
                table: "Verpflegungsanforderungen",
                columns: new[] { "AgentId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungen_Aktenzeichen",
                table: "Verpflegungsanforderungen",
                column: "Aktenzeichen",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungen_AsservatEintragId",
                table: "Verpflegungsanforderungen",
                column: "AsservatEintragId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungen_LimitWoche",
                table: "Verpflegungsanforderungen",
                column: "LimitWoche");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungen_Status",
                table: "Verpflegungsanforderungen",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungspositionen_AnforderungId",
                table: "Verpflegungsanforderungspositionen",
                column: "AnforderungId");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungsanforderungspositionen_PositionId",
                table: "Verpflegungsanforderungspositionen",
                column: "PositionId");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungspositionen_AsservatId",
                table: "Verpflegungspositionen",
                column: "AsservatId");

            migrationBuilder.CreateIndex(
                name: "IX_Verpflegungspositionen_IstAktiv",
                table: "Verpflegungspositionen",
                column: "IstAktiv");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Verpflegungsanforderungspositionen");

            migrationBuilder.DropTable(
                name: "Verpflegungsanforderungen");

            migrationBuilder.DropTable(
                name: "Verpflegungspositionen");

            migrationBuilder.DropColumn(
                name: "VerpflegungslimitEssen",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "VerpflegungslimitTrinken",
                table: "AspNetUsers");
        }
    }
}
