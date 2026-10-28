using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase101_FuehrungskartenFotos : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FotoDateiname",
                table: "OeffentlicheTelefonnummern",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "FotoTyp",
                table: "OeffentlicheTelefonnummern",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_OeffentlicheTelefonnummern_FotoDateiname",
                table: "OeffentlicheTelefonnummern",
                column: "FotoDateiname");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_OeffentlicheTelefonnummern_FotoDateiname",
                table: "OeffentlicheTelefonnummern");

            migrationBuilder.DropColumn(
                name: "FotoDateiname",
                table: "OeffentlicheTelefonnummern");

            migrationBuilder.DropColumn(
                name: "FotoTyp",
                table: "OeffentlicheTelefonnummern");
        }
    }
}
