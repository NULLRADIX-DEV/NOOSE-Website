using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase89_TaskforceChatGelesen : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "ZuletztGelesenAm",
                table: "TaskforceAgenten",
                type: "datetime(6)",
                nullable: true);

            // existing members start at zero; their join date would flag the whole history as unread
            migrationBuilder.Sql("UPDATE TaskforceAgenten SET ZuletztGelesenAm = UTC_TIMESTAMP(6);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ZuletztGelesenAm",
                table: "TaskforceAgenten");
        }
    }
}
