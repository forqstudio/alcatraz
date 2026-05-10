using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Alcatraz.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Add_Sandbox_RuntimeInfo : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "actual_memory_mib",
                table: "sandboxes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "actual_vcpus",
                table: "sandboxes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "boot_duration_ms",
                table: "sandboxes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "firecracker_pid",
                table: "sandboxes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "host_gateway_ip",
                table: "sandboxes",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "kernel_path",
                table: "sandboxes",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "mac_address",
                table: "sandboxes",
                type: "character varying(17)",
                maxLength: 17,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "nfs_port",
                table: "sandboxes",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ready_at_utc",
                table: "sandboxes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "rootfs_path",
                table: "sandboxes",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "socket_path",
                table: "sandboxes",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tap_device",
                table: "sandboxes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vm_ip",
                table: "sandboxes",
                type: "character varying(45)",
                maxLength: 45,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vmm_state",
                table: "sandboxes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "vmm_version",
                table: "sandboxes",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "worker_slot_index",
                table: "sandboxes",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "actual_memory_mib",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "actual_vcpus",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "boot_duration_ms",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "firecracker_pid",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "host_gateway_ip",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "kernel_path",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "mac_address",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "nfs_port",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "ready_at_utc",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "rootfs_path",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "socket_path",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "tap_device",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "vm_ip",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "vmm_state",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "vmm_version",
                table: "sandboxes");

            migrationBuilder.DropColumn(
                name: "worker_slot_index",
                table: "sandboxes");
        }
    }
}
