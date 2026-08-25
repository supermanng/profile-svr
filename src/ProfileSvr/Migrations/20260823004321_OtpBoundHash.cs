using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class OtpBoundHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Encrypted codes cannot be converted to bound hashes - purge them (all short-lived dev codes).
            migrationBuilder.Sql("DELETE FROM otp_codes;");

            migrationBuilder.DropColumn(
                name: "code_encrypted",
                table: "otp_codes");

            migrationBuilder.AddColumn<string>(
                name: "code_hash",
                table: "otp_codes",
                type: "varchar(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "code_hash",
                table: "otp_codes");

            migrationBuilder.AddColumn<string>(
                name: "code_encrypted",
                table: "otp_codes",
                type: "varchar(128)",
                maxLength: 128,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");
        }
    }
}
