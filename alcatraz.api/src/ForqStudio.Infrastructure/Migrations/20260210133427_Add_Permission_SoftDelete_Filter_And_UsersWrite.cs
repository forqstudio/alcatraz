using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ForqStudio.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_Permission_SoftDelete_Filter_And_UsersWrite : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "Permissions",
                columns: new[] { "id", "is_deleted", "name" },
                values: new object[] { 6, false, "users.write" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "Permissions",
                keyColumn: "id",
                keyValue: 6);
        }
    }
}
