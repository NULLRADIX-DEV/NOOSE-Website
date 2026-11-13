using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase127_Aktenfuehrer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AktenfuehrerId",
                table: "Personen",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "VertretungId",
                table: "Personen",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_Personen_AktenfuehrerId",
                table: "Personen",
                column: "AktenfuehrerId");

            migrationBuilder.CreateIndex(
                name: "IX_Personen_VertretungId",
                table: "Personen",
                column: "VertretungId");

            migrationBuilder.AddForeignKey(
                name: "FK_Personen_AspNetUsers_AktenfuehrerId",
                table: "Personen",
                column: "AktenfuehrerId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_Personen_AspNetUsers_VertretungId",
                table: "Personen",
                column: "VertretungId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Personen_AspNetUsers_AktenfuehrerId",
                table: "Personen");

            migrationBuilder.DropForeignKey(
                name: "FK_Personen_AspNetUsers_VertretungId",
                table: "Personen");

            migrationBuilder.DropIndex(
                name: "IX_Personen_AktenfuehrerId",
                table: "Personen");

            migrationBuilder.DropIndex(
                name: "IX_Personen_VertretungId",
                table: "Personen");

            migrationBuilder.DropColumn(
                name: "AktenfuehrerId",
                table: "Personen");

            migrationBuilder.DropColumn(
                name: "VertretungId",
                table: "Personen");
        }
    }
}
