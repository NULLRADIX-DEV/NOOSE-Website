using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase128_PartnerEinheiten : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IstEinheitsleitung",
                table: "AspNetUsers",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PartnerEinheitId",
                table: "AspNetUsers",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PartnerEinheiten",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Behoerde = table.Column<int>(type: "int", nullable: false),
                    Name = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Reihenfolge = table.Column<int>(type: "int", nullable: false),
                    AltRang = table.Column<int>(type: "int", nullable: false),
                    IstStaatsanwaltschaft = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    DarfErmittlungAnlegen = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    LeitungSiehtAlleErmittlungen = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    SichtbarkeitJson = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    ErmittlungVorgabenJson = table.Column<string>(type: "longtext", nullable: true)
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
                    table.PrimaryKey("PK_PartnerEinheiten", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "TaskforcePartnerbehoerden",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    TaskforceId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Behoerde = table.Column<int>(type: "int", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskforcePartnerbehoerden", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TaskforcePartnerbehoerden_Taskforces_TaskforceId",
                        column: x => x.TaskforceId,
                        principalTable: "Taskforces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PartnerEinheitFreigaben",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntitaetTyp = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntitaetId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PartnerEinheitId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    InklusiveKinder = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    GueltigBis = table.Column<DateTime>(type: "datetime(6)", nullable: true),
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
                    table.PrimaryKey("PK_PartnerEinheitFreigaben", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartnerEinheitFreigaben_PartnerEinheiten_PartnerEinheitId",
                        column: x => x.PartnerEinheitId,
                        principalTable: "PartnerEinheiten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PartnerEinheitRegeln",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    PartnerEinheitId = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    EntitaetTyp = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Umfang = table.Column<int>(type: "int", nullable: false),
                    InklusiveKinder = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerEinheitRegeln", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PartnerEinheitRegeln_PartnerEinheiten_PartnerEinheitId",
                        column: x => x.PartnerEinheitId,
                        principalTable: "PartnerEinheiten",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.InsertData(
                table: "PartnerEinheiten",
                columns: new[] { "Id", "Behoerde", "ErstelltAm", "ErstelltVonId", "GeloeschtAm", "GeloeschtVonId", "ErmittlungVorgabenJson", "IstGeloescht", "IstStaatsanwaltschaft", "LeitungSiehtAlleErmittlungen", "AltRang", "DarfErmittlungAnlegen", "GeaendertAm", "GeaendertVonId", "Name", "Reihenfolge", "SichtbarkeitJson" },
                values: new object[,]
                {
                    { "5e0d7a10-0101-4c1e-9a00-000000000101", 1, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, true, 1, true, null, null, "SID", 1, null },
                    { "5e0d7a10-0102-4c1e-9a00-000000000102", 1, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, true, false, 2, false, null, null, "State Attorney Office", 2, null },
                    { "5e0d7a10-0103-4c1e-9a00-000000000103", 1, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 3, false, null, null, "Chief", 3, null },
                    { "5e0d7a10-0201-4c1e-9a00-000000000201", 2, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 1, false, null, null, "Mitglied", 1, null },
                    { "5e0d7a10-0202-4c1e-9a00-000000000202", 2, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 2, false, null, null, "Special", 2, null },
                    { "5e0d7a10-0203-4c1e-9a00-000000000203", 2, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 3, false, null, null, "Chief", 3, null },
                    { "5e0d7a10-0301-4c1e-9a00-000000000301", 3, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 1, false, null, null, "Mitglied", 1, null },
                    { "5e0d7a10-0302-4c1e-9a00-000000000302", 3, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 2, false, null, null, "Special", 2, null },
                    { "5e0d7a10-0303-4c1e-9a00-000000000303", 3, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 3, false, null, null, "Chief", 3, null },
                    { "5e0d7a10-0401-4c1e-9a00-000000000401", 4, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 1, false, null, null, "Mitglied", 1, null },
                    { "5e0d7a10-0402-4c1e-9a00-000000000402", 4, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 2, false, null, null, "Special", 2, null },
                    { "5e0d7a10-0403-4c1e-9a00-000000000403", 4, new DateTime(2026, 11, 13, 0, 0, 0, 0, DateTimeKind.Utc), null, null, null, null, false, false, false, 3, false, null, null, "Chief", 3, null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_PartnerEinheitId",
                table: "AspNetUsers",
                column: "PartnerEinheitId");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerEinheiten_Behoerde_Reihenfolge",
                table: "PartnerEinheiten",
                columns: new[] { "Behoerde", "Reihenfolge" });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerEinheitFreigaben_EntitaetTyp_EntitaetId",
                table: "PartnerEinheitFreigaben",
                columns: new[] { "EntitaetTyp", "EntitaetId" });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerEinheitFreigaben_PartnerEinheitId_EntitaetTyp",
                table: "PartnerEinheitFreigaben",
                columns: new[] { "PartnerEinheitId", "EntitaetTyp" });

            migrationBuilder.CreateIndex(
                name: "IX_PartnerEinheitRegeln_PartnerEinheitId_EntitaetTyp",
                table: "PartnerEinheitRegeln",
                columns: new[] { "PartnerEinheitId", "EntitaetTyp" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TaskforcePartnerbehoerden_Behoerde",
                table: "TaskforcePartnerbehoerden",
                column: "Behoerde");

            migrationBuilder.CreateIndex(
                name: "IX_TaskforcePartnerbehoerden_TaskforceId_Behoerde",
                table: "TaskforcePartnerbehoerden",
                columns: new[] { "TaskforceId", "Behoerde" },
                unique: true);

            // every partner account to the seeded unit of its (agency, tier); no tier = base unit (ids: PartnerUnits.ForLegacy)
            migrationBuilder.Sql(
                "UPDATE `AspNetUsers` SET `PartnerEinheitId` = CONCAT('5e0d7a10-0', `Partnerbehoerde`, '0', COALESCE(`Partnerrang`, 1), " +
                "'-4c1e-9a00-00000000', LPAD(`Partnerbehoerde`, 2, '0'), LPAD(COALESCE(`Partnerrang`, 1), 2, '0')) " +
                "WHERE `Partnerbehoerde` IS NOT NULL AND `PartnerEinheitId` IS NULL;");

            migrationBuilder.AddForeignKey(
                name: "FK_AspNetUsers_PartnerEinheiten_PartnerEinheitId",
                table: "AspNetUsers",
                column: "PartnerEinheitId",
                principalTable: "PartnerEinheiten",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AspNetUsers_PartnerEinheiten_PartnerEinheitId",
                table: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "PartnerEinheitFreigaben");

            migrationBuilder.DropTable(
                name: "PartnerEinheitRegeln");

            migrationBuilder.DropTable(
                name: "TaskforcePartnerbehoerden");

            migrationBuilder.DropTable(
                name: "PartnerEinheiten");

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_PartnerEinheitId",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "IstEinheitsleitung",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "PartnerEinheitId",
                table: "AspNetUsers");
        }
    }
}
