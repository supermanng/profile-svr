using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddUserDevicesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_devices",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    profile_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    device_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    linked_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    released_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_devices", x => x.id);
                    table.ForeignKey(
                        name: "FK_user_devices_devices_device_id",
                        column: x => x.device_id,
                        principalTable: "devices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_user_devices_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_user_devices_device_id_released_at_utc",
                table: "user_devices",
                columns: new[] { "device_id", "released_at_utc" });

            migrationBuilder.CreateIndex(
                name: "IX_user_devices_profile_id_released_at_utc",
                table: "user_devices",
                columns: new[] { "profile_id", "released_at_utc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_devices");
        }
    }
}
