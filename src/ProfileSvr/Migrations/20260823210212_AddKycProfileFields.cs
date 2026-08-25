using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddKycProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "address",
                table: "profiles",
                type: "varchar(256)",
                maxLength: 256,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "bvn",
                table: "profiles",
                type: "varchar(11)",
                maxLength: 11,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "bvn_is_verified",
                table: "profiles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "cif",
                table: "profiles",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "gender",
                table: "profiles",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "nin",
                table: "profiles",
                type: "varchar(11)",
                maxLength: 11,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "nin_is_verified",
                table: "profiles",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "tier",
                table: "profiles",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "address",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "bvn",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "bvn_is_verified",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "cif",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "gender",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "nin",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "nin_is_verified",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "tier",
                table: "profiles");
        }
    }
}
