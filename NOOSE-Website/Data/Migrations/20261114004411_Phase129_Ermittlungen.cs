using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase129_Ermittlungen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntscheiderFunktion",
                table: "Anordnungen",
                type: "varchar(160)",
                maxLength: 160,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "ErmittlungId",
                table: "Anordnungen",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "Ermittlungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Aktenzeichen = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Titel = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaskforceId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AzSid = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Az5Net = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    EroeffnetAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    Anklage = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TathergangHtml = table.Column<string>(type: "longtext", nullable: true)
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
                    table.PrimaryKey("PK_Ermittlungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Ermittlungen_Taskforces_TaskforceId",
                        column: x => x.TaskforceId,
                        principalTable: "Taskforces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungAufgaben",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Erledigt = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErledigtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ErledigtVonId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ZustaendigerAgentId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FaelligAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungAufgaben", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungAufgaben_AspNetUsers_ZustaendigerAgentId",
                        column: x => x.ZustaendigerAgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungAufgaben_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungBeteiligte",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PartnerEinheitId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rolle = table.Column<int>(type: "int", nullable: false),
                    TabRechteJson = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungBeteiligte", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungBeteiligte_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungBeteiligte_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungBeteiligte_PartnerEinheiten_PartnerEinheitId",
                        column: x => x.PartnerEinheitId,
                        principalTable: "PartnerEinheiten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungBeweismittel",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Nummer = table.Column<int>(type: "int", nullable: false),
                    Bezeichnung = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Besitzer = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beamter = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Datum = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungBeweismittel", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungBeweismittel_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungEintragFreigaben",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EintragTyp = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EintragId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AgentId = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PartnerEinheitId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
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
                    table.PrimaryKey("PK_ErmittlungEintragFreigaben", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungEintragFreigaben_AspNetUsers_AgentId",
                        column: x => x.AgentId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungEintragFreigaben_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungEintragFreigaben_PartnerEinheiten_PartnerEinheitId",
                        column: x => x.PartnerEinheitId,
                        principalTable: "PartnerEinheiten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungNotizen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Text = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    AutorName = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungNotizen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungNotizen_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungPersonen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PersonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Telefon = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rolle = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Status = table.Column<int>(type: "int", nullable: false),
                    Notiz = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungPersonen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungPersonen_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungPersonen_Personen_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Personen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungTatvorwuerfe",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GesetzId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Paragraf = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Gesetzbuch = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Pruefung = table.Column<int>(type: "int", nullable: false),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungTatvorwuerfe", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungTatvorwuerfe_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungVernehmungen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungPersonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rolle = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Vorladung = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Zeitpunkt = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    Beamte = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StaatsanwaltAnwesend = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Zusammenfassung = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ProtokollVerweis = table.Column<string>(type: "varchar(500)", maxLength: 500, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Verwertbar = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErmittlungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungVernehmungen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungVernehmungen_ErmittlungPersonen_ErmittlungPersonId",
                        column: x => x.ErmittlungPersonId,
                        principalTable: "ErmittlungPersonen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ErmittlungVernehmungen_Ermittlungen_ErmittlungId",
                        column: x => x.ErmittlungId,
                        principalTable: "Ermittlungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "ErmittlungVernehmungFragen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    VernehmungId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    Frage = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Antwort = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: false)
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
                    table.PrimaryKey("PK_ErmittlungVernehmungFragen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ErmittlungVernehmungFragen_ErmittlungVernehmungen_Vernehmung~",
                        column: x => x.VernehmungId,
                        principalTable: "ErmittlungVernehmungen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Anordnungen_ErmittlungId",
                table: "Anordnungen",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungAufgaben_ErmittlungId",
                table: "ErmittlungAufgaben",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungAufgaben_ZustaendigerAgentId",
                table: "ErmittlungAufgaben",
                column: "ZustaendigerAgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungBeteiligte_AgentId",
                table: "ErmittlungBeteiligte",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungBeteiligte_ErmittlungId_AgentId",
                table: "ErmittlungBeteiligte",
                columns: new[] { "ErmittlungId", "AgentId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungBeteiligte_PartnerEinheitId",
                table: "ErmittlungBeteiligte",
                column: "PartnerEinheitId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungBeweismittel_ErmittlungId",
                table: "ErmittlungBeweismittel",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungEintragFreigaben_AgentId",
                table: "ErmittlungEintragFreigaben",
                column: "AgentId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungEintragFreigaben_EintragTyp_EintragId",
                table: "ErmittlungEintragFreigaben",
                columns: new[] { "EintragTyp", "EintragId" });

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungEintragFreigaben_ErmittlungId",
                table: "ErmittlungEintragFreigaben",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungEintragFreigaben_PartnerEinheitId",
                table: "ErmittlungEintragFreigaben",
                column: "PartnerEinheitId");

            migrationBuilder.CreateIndex(
                name: "IX_Ermittlungen_Aktenzeichen",
                table: "Ermittlungen",
                column: "Aktenzeichen",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Ermittlungen_Status",
                table: "Ermittlungen",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Ermittlungen_TaskforceId",
                table: "Ermittlungen",
                column: "TaskforceId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungNotizen_ErmittlungId",
                table: "ErmittlungNotizen",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungPersonen_ErmittlungId",
                table: "ErmittlungPersonen",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungPersonen_PersonId",
                table: "ErmittlungPersonen",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungTatvorwuerfe_ErmittlungId",
                table: "ErmittlungTatvorwuerfe",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungVernehmungen_ErmittlungId",
                table: "ErmittlungVernehmungen",
                column: "ErmittlungId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungVernehmungen_ErmittlungPersonId",
                table: "ErmittlungVernehmungen",
                column: "ErmittlungPersonId");

            migrationBuilder.CreateIndex(
                name: "IX_ErmittlungVernehmungFragen_VernehmungId_Reihenfolge",
                table: "ErmittlungVernehmungFragen",
                columns: new[] { "VernehmungId", "Reihenfolge" });

            migrationBuilder.AddForeignKey(
                name: "FK_Anordnungen_Ermittlungen_ErmittlungId",
                table: "Anordnungen",
                column: "ErmittlungId",
                principalTable: "Ermittlungen",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Anordnungen_Ermittlungen_ErmittlungId",
                table: "Anordnungen");

            migrationBuilder.DropTable(
                name: "ErmittlungAufgaben");

            migrationBuilder.DropTable(
                name: "ErmittlungBeteiligte");

            migrationBuilder.DropTable(
                name: "ErmittlungBeweismittel");

            migrationBuilder.DropTable(
                name: "ErmittlungEintragFreigaben");

            migrationBuilder.DropTable(
                name: "ErmittlungNotizen");

            migrationBuilder.DropTable(
                name: "ErmittlungTatvorwuerfe");

            migrationBuilder.DropTable(
                name: "ErmittlungVernehmungFragen");

            migrationBuilder.DropTable(
                name: "ErmittlungVernehmungen");

            migrationBuilder.DropTable(
                name: "ErmittlungPersonen");

            migrationBuilder.DropTable(
                name: "Ermittlungen");

            migrationBuilder.DropIndex(
                name: "IX_Anordnungen_ErmittlungId",
                table: "Anordnungen");

            migrationBuilder.DropColumn(
                name: "EntscheiderFunktion",
                table: "Anordnungen");

            migrationBuilder.DropColumn(
                name: "ErmittlungId",
                table: "Anordnungen");
        }
    }
}
