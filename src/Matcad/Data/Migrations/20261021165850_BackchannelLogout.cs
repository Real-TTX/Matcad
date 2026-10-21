using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Matcad.Data.Migrations
{
    /// <inheritdoc />
    public partial class BackchannelLogout : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExternalSessionId",
                table: "UserSession",
                type: "TEXT",
                maxLength: 256,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IxUserSessionExternalSessionId",
                table: "UserSession",
                column: "ExternalSessionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IxUserSessionExternalSessionId",
                table: "UserSession");

            migrationBuilder.DropColumn(
                name: "ExternalSessionId",
                table: "UserSession");
        }
    }
}
