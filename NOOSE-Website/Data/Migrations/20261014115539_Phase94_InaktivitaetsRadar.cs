using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase94_InaktivitaetsRadar : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "AktivSeitAm",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "InaktivNachgefasstAm",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "InaktivNachgefasstVonName",
                table: "AspNetUsers",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "ZuletztGesehenAm",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AgentId_Zeitpunkt",
                table: "AuditLogs",
                columns: new[] { "AgentId", "Zeitpunkt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AuditLogs_AgentId_Zeitpunkt",
                table: "AuditLogs");

            migrationBuilder.DropColumn(
                name: "AktivSeitAm",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "InaktivNachgefasstAm",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "InaktivNachgefasstVonName",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "ZuletztGesehenAm",
                table: "AspNetUsers");
        }
    }
}
