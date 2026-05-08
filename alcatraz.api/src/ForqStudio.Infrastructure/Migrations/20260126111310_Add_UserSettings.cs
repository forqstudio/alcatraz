using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForqStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_UserSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_settings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    preferred_language = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    email_notifications_enabled = table.Column<bool>(type: "boolean", nullable: true),
                    timezone = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_user_settings", x => x.id);
                    table.ForeignKey(
                        name: "fk_user_settings_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_user_settings_user_id",
                table: "user_settings",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_settings");
        }
    }
}
