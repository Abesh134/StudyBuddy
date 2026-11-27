using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace StudyBuddy.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailNotifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "INTEGER", nullable: false)
                        .Annotation("Sqlite:Autoincrement", true),
                    UserId = table.Column<string>(type: "TEXT", maxLength: 450, nullable: false),
                    NotificationType = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "INTEGER", nullable: false),
                    ProviderMessageId = table.Column<string>(type: "TEXT", maxLength: 128, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false),
                    AcceptedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: true),
                    AttemptCount = table.Column<int>(type: "INTEGER", nullable: false),
                    LastErrorCode = table.Column<string>(type: "TEXT", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailNotifications", x => x.Id);
                    table.CheckConstraint("CK_EmailNotifications_AttemptCount", "[AttemptCount] >= 0");
                    table.ForeignKey(
                        name: "FK_EmailNotifications_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_EmailNotifications_Status_CreatedAtUtc",
                table: "EmailNotifications",
                columns: new[] { "Status", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "UX_EmailNotifications_UserId_NotificationType",
                table: "EmailNotifications",
                columns: new[] { "UserId", "NotificationType" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailNotifications");
        }
    }
}
