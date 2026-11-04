using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase115_KalenderAbo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "KalenderAboErstelltAm",
                table: "AspNetUsers",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "KalenderAboHash",
                table: "AspNetUsers",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "KalenderAboNeutral",
                table: "AspNetUsers",
                type: "tinyint(1)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_KalenderAboHash",
                table: "AspNetUsers",
                column: "KalenderAboHash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_KalenderAboHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "KalenderAboErstelltAm",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "KalenderAboHash",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "KalenderAboNeutral",
                table: "AspNetUsers");
        }
    }
}
