using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcatraz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_Sandbox_Endpoint : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "host",
                table: "sandboxes",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "port",
                table: "sandboxes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "host",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "port",
                table: "sandboxes");
        }
    }
}
