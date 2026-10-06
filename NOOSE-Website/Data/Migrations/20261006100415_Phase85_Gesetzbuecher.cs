using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase85_Gesetzbuecher : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Abschnitt",
                table: "Gesetze",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "Reihenfolge",
                table: "Gesetze",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Gesetzbuecher",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Kuerzel = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Name = table.Column<string>(type: "varchar(256)", maxLength: 256, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Beschreibung = table.Column<string>(type: "varchar(1024)", maxLength: 1024, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_Gesetzbuecher", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Gesetzbuecher_Kuerzel",
                table: "Gesetzbuecher",
                column: "Kuerzel");

            // plain text to paragraphs, as HtmlCleanup.FromPlain does: escape, trim lines, drop blank ones
            migrationBuilder.Sql(@"
UPDATE Gesetze SET Text = CONCAT('<p>', REPLACE(
    REGEXP_REPLACE(
        REGEXP_REPLACE(
            REPLACE(REPLACE(REPLACE(REPLACE(Text, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), CHAR(13 USING utf8mb4), ''),
            CONCAT('[ ', CHAR(9 USING utf8mb4), ']*', CHAR(10 USING utf8mb4), '[[:space:]]*'), CHAR(10 USING utf8mb4)),
        '^[[:space:]]+|[[:space:]]+$', ''),
    CHAR(10 USING utf8mb4), '</p><p>'), '</p>')
WHERE Text NOT LIKE '<%' AND TRIM(Text) <> '';");

            // one catalog row per book the paragraphs already name; the collation folds BtMG and BtmG together
            migrationBuilder.Sql(@"
INSERT INTO Gesetzbuecher (Id, Kuerzel, Name, Beschreibung, Reihenfolge, ErstelltAm, IstGeloescht)
SELECT UUID(), MIN(Gesetzbuch), MIN(Gesetzbuch), NULL, 0, UTC_TIMESTAMP(6), 0
FROM Gesetze
WHERE IstGeloescht = 0
GROUP BY Gesetzbuch;");

            migrationBuilder.Sql(@"
UPDATE Gesetzbuecher b
JOIN (SELECT Id, ROW_NUMBER() OVER (ORDER BY Kuerzel) AS rn FROM Gesetzbuecher) x ON x.Id = b.Id
SET b.Reihenfolge = x.rn;");

            migrationBuilder.Sql(@"
UPDATE Gesetze g
JOIN Gesetzbuecher b ON g.Gesetzbuch = b.Kuerzel
SET g.Gesetzbuch = b.Kuerzel;");

            // reading order by the paragraph number; a paragraph without one goes first
            migrationBuilder.Sql(@"
UPDATE Gesetze g
JOIN (
    SELECT Id, ROW_NUMBER() OVER (
        PARTITION BY Gesetzbuch
        ORDER BY CAST(COALESCE(NULLIF(REGEXP_SUBSTR(Paragraf, '[0-9]+'), ''), '0') AS UNSIGNED), Paragraf, Titel) AS rn
    FROM Gesetze
) x ON x.Id = g.Id
SET g.Reihenfolge = x.rn;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Gesetzbuecher");

            migrationBuilder.DropColumn(
                name: "Abschnitt",
                table: "Gesetze");

            migrationBuilder.DropColumn(
                name: "Reihenfolge",
                table: "Gesetze");
        }
    }
}
