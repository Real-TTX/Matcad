using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Matcad.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "RequestLog",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Host = table.Column<string>(type: "TEXT", nullable: false),
                    Path = table.Column<string>(type: "TEXT", nullable: false),
                    Method = table.Column<string>(type: "TEXT", nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    RemoteIp = table.Column<string>(type: "TEXT", nullable: false),
                    DurationMs = table.Column<double>(type: "REAL", nullable: false),
                    Timestamp = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PkRequestLog", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "User",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Username = table.Column<string>(type: "TEXT", maxLength: 128, nullable: false, collation: "NOCASE"),
                    Email = table.Column<string>(type: "TEXT", maxLength: 256, nullable: true, collation: "NOCASE"),
                    PasswordHash = table.Column<string>(type: "TEXT", maxLength: 100, nullable: false),
                    Role = table.Column<int>(type: "INTEGER", nullable: false),
                    MustChangePassword = table.Column<bool>(type: "INTEGER", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PkUser", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserSession",
                columns: table => new
                {
                    Id = table.Column<long>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    Token = table.Column<Guid>(type: "TEXT", nullable: false),
                    UserId = table.Column<long>(type: "INTEGER", nullable: false),
                    ExpiryDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateDate = table.Column<DateTime>(type: "TEXT", nullable: false),
                    CreateUserId = table.Column<long>(type: "INTEGER", nullable: true),
                    UpdateDate = table.Column<DateTime>(type: "TEXT", nullable: true),
                    UpdateUserId = table.Column<long>(type: "INTEGER", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PkUserSession", x => x.Id);
                    table.ForeignKey(
                        name: "FkUserSessionUser",
                        column: x => x.UserId,
                        principalTable: "User",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IxRequestLogHostTimestamp",
                table: "RequestLog",
                columns: new[] { "Host", "Timestamp" });

            migrationBuilder.CreateIndex(
                name: "IxRequestLogTimestamp",
                table: "RequestLog",
                column: "Timestamp");

            migrationBuilder.CreateIndex(
                name: "IxUserEmail",
                table: "User",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IxUserUsername",
                table: "User",
                column: "Username",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IxUserSessionExpiryDate",
                table: "UserSession",
                column: "ExpiryDate");

            migrationBuilder.CreateIndex(
                name: "IxUserSessionToken",
                table: "UserSession",
                column: "Token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IxUserSessionUserId",
                table: "UserSession",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "RequestLog");

            migrationBuilder.DropTable(
                name: "UserSession");

            migrationBuilder.DropTable(
                name: "User");
        }
    }
}
