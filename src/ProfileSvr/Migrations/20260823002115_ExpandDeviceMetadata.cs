using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class ExpandDeviceMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "app_version",
                table: "devices",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "manufacturer",
                table: "devices",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "model",
                table: "devices",
                type: "varchar(128)",
                maxLength: 128,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "os_name",
                table: "devices",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "os_version",
                table: "devices",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at_utc",
                table: "devices",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "app_version",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "manufacturer",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "model",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "os_name",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "os_version",
                table: "devices");

            migrationBuilder.DropColumn(
                name: "updated_at_utc",
                table: "devices");
        }
    }
}
