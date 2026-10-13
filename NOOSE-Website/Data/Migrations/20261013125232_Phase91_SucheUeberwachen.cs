using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase91_SucheUeberwachen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BekannteTrefferJson",
                table: "GespeicherteSuchen",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "LetzterLaufUnvollstaendig",
                table: "GespeicherteSuchen",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "NeueTrefferJson",
                table: "GespeicherteSuchen",
                type: "longtext",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "RechteStempel",
                table: "GespeicherteSuchen",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "Ueberwacht",
                table: "GespeicherteSuchen",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "ZuletztGeprueftAm",
                table: "GespeicherteSuchen",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_GespeicherteSuchen_Ueberwacht_ZuletztGeprueftAm",
                table: "GespeicherteSuchen",
                columns: new[] { "Ueberwacht", "ZuletztGeprueftAm" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_GespeicherteSuchen_Ueberwacht_ZuletztGeprueftAm",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "BekannteTrefferJson",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "LetzterLaufUnvollstaendig",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "NeueTrefferJson",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "RechteStempel",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "Ueberwacht",
                table: "GespeicherteSuchen");

            migrationBuilder.DropColumn(
                name: "ZuletztGeprueftAm",
                table: "GespeicherteSuchen");
        }
    }
}
