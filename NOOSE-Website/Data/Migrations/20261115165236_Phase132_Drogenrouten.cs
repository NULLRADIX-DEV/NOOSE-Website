using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase132_Drogenrouten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Drogenrouten",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Aktenzeichen = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Bezeichnung = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Substanz = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Erkenntnisse = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Bemerkungen = table.Column<string>(type: "varchar(2000)", maxLength: 2000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    FraktionId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Einstufung = table.Column<int>(type: "int", nullable: false),
                    IstVerschlusssache = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IstVerschlusssacheTRU = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    IstVerschlusssacheHRB = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    VeralterungDeaktiviert = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IstGeloescht = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GeloeschtAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeloeschtVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    IstArchiviert = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ArchiviertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    ArchiviertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Archivgrund = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Drogenrouten", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Drogenrouten_Fraktionen_FraktionId",
                        column: x => x.FraktionId,
                        principalTable: "Fraktionen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DrogenroutenFotos",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DrogenrouteId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    StationId = table.Column<string>(type: "varchar(255)", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Bildunterschrift = table.Column<string>(type: "varchar(300)", maxLength: 300, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DateinameGespeichert = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    OriginalName = table.Column<string>(type: "varchar(260)", maxLength: 260, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ContentType = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GroesseBytes = table.Column<long>(type: "bigint", nullable: false),
                    IstTitelbild = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: true)
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
                    table.PrimaryKey("PK_DrogenroutenFotos", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrogenroutenFotos_Drogenrouten_DrogenrouteId",
                        column: x => x.DrogenrouteId,
                        principalTable: "Drogenrouten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DrogenroutenPersonen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DrogenrouteId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PersonId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Rolle = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
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
                    table.PrimaryKey("PK_DrogenroutenPersonen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrogenroutenPersonen_Drogenrouten_DrogenrouteId",
                        column: x => x.DrogenrouteId,
                        principalTable: "Drogenrouten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DrogenroutenPersonen_Personen_PersonId",
                        column: x => x.PersonId,
                        principalTable: "Personen",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "DrogenroutenStationen",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    DrogenrouteId = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    Art = table.Column<int>(type: "int", nullable: false),
                    Ort = table.Column<string>(type: "varchar(200)", maxLength: 200, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(4000)", maxLength: 4000, nullable: true)
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
                    table.PrimaryKey("PK_DrogenroutenStationen", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DrogenroutenStationen_Drogenrouten_DrogenrouteId",
                        column: x => x.DrogenrouteId,
                        principalTable: "Drogenrouten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Drogenrouten_Aktenzeichen",
                table: "Drogenrouten",
                column: "Aktenzeichen",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Drogenrouten_Bezeichnung",
                table: "Drogenrouten",
                column: "Bezeichnung");

            migrationBuilder.CreateIndex(
                name: "IX_Drogenrouten_FraktionId",
                table: "Drogenrouten",
                column: "FraktionId");

            migrationBuilder.CreateIndex(
                name: "IX_Drogenrouten_IstArchiviert",
                table: "Drogenrouten",
                column: "IstArchiviert");

            migrationBuilder.CreateIndex(
                name: "IX_Drogenrouten_IstVerschlusssache",
                table: "Drogenrouten",
                column: "IstVerschlusssache");

            migrationBuilder.CreateIndex(
                name: "IX_DrogenroutenFotos_DrogenrouteId",
                table: "DrogenroutenFotos",
                column: "DrogenrouteId");

            migrationBuilder.CreateIndex(
                name: "IX_DrogenroutenFotos_StationId",
                table: "DrogenroutenFotos",
                column: "StationId");

            migrationBuilder.CreateIndex(
                name: "IX_DrogenroutenPersonen_DrogenrouteId_PersonId",
                table: "DrogenroutenPersonen",
                columns: new[] { "DrogenrouteId", "PersonId" });

            migrationBuilder.CreateIndex(
                name: "IX_DrogenroutenPersonen_PersonId",
                table: "DrogenroutenPersonen",
                column: "PersonId");

            migrationBuilder.CreateIndex(
                name: "IX_DrogenroutenStationen_DrogenrouteId_Reihenfolge",
                table: "DrogenroutenStationen",
                columns: new[] { "DrogenrouteId", "Reihenfolge" });

            // every legacy route row becomes a file; a faction in trash or archive hands its routes to the archive,
            // so only routes of active factions compete for a name. The old table stays for a rollback.
            migrationBuilder.Sql("""
                INSERT INTO Drogenrouten (Id, Aktenzeichen, Bezeichnung, Beschreibung, FraktionId, Einstufung,
                    IstVerschlusssache, IstVerschlusssacheTRU, IstVerschlusssacheHRB, VeralterungDeaktiviert,
                    ErstelltAm, IstGeloescht, IstArchiviert, ArchiviertAm, Archivgrund)
                SELECT UUID(),
                    CONCAT('FIB-DR-', YEAR(UTC_TIMESTAMP()), '-', LPAD(x.Nr, 4, '0')),
                    x.Bezeichnung, x.Notiz, x.FraktionId, 0,
                    x.IstVerschlusssache, x.IstVerschlusssacheTRU, x.IstVerschlusssacheHRB, 0,
                    UTC_TIMESTAMP(), 0,
                    x.Abgelegt, CASE WHEN x.Abgelegt = 1 THEN UTC_TIMESTAMP() END,
                    CASE WHEN x.Abgelegt = 1 THEN 'Fraktion war bei der Umstellung auf Drogenroutenakten abgelegt' END
                FROM (
                    SELECT d.Bezeichnung, d.Notiz, d.FraktionId,
                        f.IstVerschlusssache, f.IstVerschlusssacheTRU, f.IstVerschlusssacheHRB,
                        (f.IstArchiviert = 1 OR f.IstGeloescht = 1) AS Abgelegt,
                        ROW_NUMBER() OVER (ORDER BY f.Name, d.Bezeichnung, d.Id) AS Nr
                    FROM FraktionDrogenrouten d
                    JOIN Fraktionen f ON f.Id = d.FraktionId
                ) x;
                """);
            migrationBuilder.Sql("""
                INSERT INTO AktenzeichenZaehler (Praefix, Jahr, LetzteNummer)
                SELECT 'DR', YEAR(UTC_TIMESTAMP()), COUNT(*) FROM Drogenrouten HAVING COUNT(*) > 0
                ON DUPLICATE KEY UPDATE LetzteNummer = GREATEST(LetzteNummer, VALUES(LetzteNummer));
                """);
            // route names are no autocomplete list any more, the files are
            migrationBuilder.Sql("DELETE FROM SteckbriefVorschlaege WHERE Typ = 8;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DrogenroutenFotos");

            migrationBuilder.DropTable(
                name: "DrogenroutenPersonen");

            migrationBuilder.DropTable(
                name: "DrogenroutenStationen");

            migrationBuilder.DropTable(
                name: "Drogenrouten");
        }
    }
}
