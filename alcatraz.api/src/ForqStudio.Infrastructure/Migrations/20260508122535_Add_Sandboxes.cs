using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForqStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_Sandboxes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sandboxes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    requested_vcpus = table.Column<int>(type: "integer", nullable: false),
                    requested_memory_mib = table.Column<int>(type: "integer", nullable: false),
                    status = table.Column<int>(type: "integer", nullable: false),
                    created_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    deleted_on_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandboxes", x => x.id);
                    table.ForeignKey(
                        name: "fk_sandboxes_user_owner_user_id",
                        column: x => x.owner_user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sandboxes_owner_user_id",
                table: "sandboxes",
                column: "owner_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_sandboxes_status",
                table: "sandboxes",
                column: "status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sandboxes");
        }
    }
}
