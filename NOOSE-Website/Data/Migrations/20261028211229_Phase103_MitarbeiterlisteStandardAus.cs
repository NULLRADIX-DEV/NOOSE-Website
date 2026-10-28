using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NOOSE_Website.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase103_MitarbeiterlisteStandardAus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // the roster is opt-in now: everyone starts off and switches themselves on in the profile
            migrationBuilder.Sql("UPDATE `AspNetUsers` SET `AufMitarbeiterlisteVerborgen` = 1;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // nothing to restore: the earlier choices are not kept anywhere
        }
    }
}
