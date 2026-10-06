using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase86_PartnerBehoerdenprofil : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IstBadfrak",
                table: "Fraktionen",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "PartnerBehoerdenProfile",
                columns: table => new
                {
                    Behoerde = table.Column<int>(type: "int", nullable: false),
                    Funktionen = table.Column<int>(type: "int", nullable: false),
                    GesperrteInhalte = table.Column<int>(type: "int", nullable: false),
                    ErstelltAm = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    ErstelltVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    GeaendertAm = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    GeaendertVonId = table.Column<string>(type: "longtext", nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PartnerBehoerdenProfile", x => x.Behoerde);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "PartnerRegelFreigaben",
                columns: table => new
                {
                    Id = table.Column<string>(type: "varchar(255)", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    Behoerde = table.Column<int>(type: "int", nullable: false),
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
                    table.PrimaryKey("PK_PartnerRegelFreigaben", x => x.Id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_PartnerRegelFreigaben_Behoerde_EntitaetTyp",
                table: "PartnerRegelFreigaben",
                columns: new[] { "Behoerde", "EntitaetTyp" },
                unique: true);

            // non-state = Badfrak
            migrationBuilder.Sql("UPDATE Fraktionen SET IstBadfrak = 1 WHERE IstStaatsfraktion = 0;");

            // Parlament defaults
            var seededAt = new DateTime(2026, 10, 6, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "PartnerBehoerdenProfile",
                columns: new[] { "Behoerde", "Funktionen", "GesperrteInhalte", "ErstelltAm" },
                values: new object[] { 4, 31, 31, seededAt });

            migrationBuilder.InsertData(
                table: "PartnerRegelFreigaben",
                columns: new[] { "Id", "Behoerde", "EntitaetTyp", "Umfang", "InklusiveKinder", "ErstelltAm" },
                values: new object[,]
                {
                    { "5f0c2a61-7d3e-4b8a-9c1f-0a6e2d4b8c01", 4, "Faction", 2, true, seededAt },
                    { "5f0c2a61-7d3e-4b8a-9c1f-0a6e2d4b8c02", 4, "Person", 3, true, seededAt },
                    { "5f0c2a61-7d3e-4b8a-9c1f-0a6e2d4b8c03", 4, "PersonGroup", 1, true, seededAt },
                    { "5f0c2a61-7d3e-4b8a-9c1f-0a6e2d4b8c04", 4, "Party", 1, true, seededAt },
                    { "5f0c2a61-7d3e-4b8a-9c1f-0a6e2d4b8c05", 4, "Law", 1, true, seededAt },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PartnerBehoerdenProfile");

            migrationBuilder.DropTable(
                name: "PartnerRegelFreigaben");

            migrationBuilder.DropColumn(
                name: "IstBadfrak",
                table: "Fraktionen");
        }
    }
}
