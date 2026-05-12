using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcatraz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_SandboxUsage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "sandbox_usage_records",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    billing_window_start_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    billing_window_end_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    provisioned_vcpu_seconds = table.Column<long>(type: "bigint", nullable: false),
                    provisioned_memory_mib_seconds = table.Column<long>(type: "bigint", nullable: false),
                    actual_cpu_usage_usec = table.Column<long>(type: "bigint", nullable: true),
                    actual_net_rx_bytes = table.Column<long>(type: "bigint", nullable: true),
                    actual_net_tx_bytes = table.Column<long>(type: "bigint", nullable: true),
                    sample_count = table.Column<int>(type: "integer", nullable: false),
                    finalised_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_usage_records", x => x.id);
                    table.ForeignKey(
                        name: "fk_sandbox_usage_records_sandboxes_id",
                        column: x => x.id,
                        principalTable: "sandboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sandbox_usage_samples",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    sandbox_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sampled_at_utc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    cpu_usage_usec_cumulative = table.Column<long>(type: "bigint", nullable: true),
                    net_rx_bytes_cumulative = table.Column<long>(type: "bigint", nullable: true),
                    net_tx_bytes_cumulative = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_sandbox_usage_samples", x => x.id);
                    table.ForeignKey(
                        name: "fk_sandbox_usage_samples_sandboxes_sandbox_id",
                        column: x => x.sandbox_id,
                        principalTable: "sandboxes",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_sandbox_usage_samples_sandbox_id_sampled_at_utc",
                table: "sandbox_usage_samples",
                columns: new[] { "sandbox_id", "sampled_at_utc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "sandbox_usage_records");

            migrationBuilder.DropTable(
                name: "sandbox_usage_samples");
        }
    }
}
