using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddDeviceTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "profiles",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.CreateTable(
                name: "devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    device_identifier = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(128)", maxLength: 128, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    platform = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_devices", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_profiles_device_id",
                table: "profiles",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_devices_device_identifier",
                table: "devices",
                column: "device_identifier",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_profiles_devices_device_id",
                table: "profiles",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_profiles_devices_device_id",
                table: "profiles");

            migrationBuilder.DropTable(
                name: "devices");

            migrationBuilder.DropIndex(
                name: "IX_profiles_device_id",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "profiles");
        }
    }
}
