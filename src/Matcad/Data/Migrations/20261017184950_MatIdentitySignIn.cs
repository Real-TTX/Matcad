using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Matcad.Data.Migrations
{
    /// <inheritdoc />
    public partial class MatIdentitySignIn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IdToken",
                table: "UserSession",
                type: "TEXT",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalSubject",
                table: "User",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IxUserExternalSubject",
                table: "User",
                column: "ExternalSubject",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IxUserExternalSubject",
                table: "User");

            migrationBuilder.DropColumn(
                name: "IdToken",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "ExternalSubject",
                table: "User");
        }
    }
}
