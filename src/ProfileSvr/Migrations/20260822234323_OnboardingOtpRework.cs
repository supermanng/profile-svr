using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class OnboardingOtpRework : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                table: "profiles",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<Guid>(
                name: "profile_id",
                table: "otp_codes",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)")
                .OldAnnotation("Relational:Collation", "ascii_general_ci");

            migrationBuilder.AddColumn<Guid>(
                name: "device_id",
                table: "otp_codes",
                type: "char(36)",
                nullable: true,
                collation: "ascii_general_ci");

            migrationBuilder.AddColumn<string>(
                name: "purpose",
                table: "otp_codes",
                type: "varchar(24)",
                maxLength: 24,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "target",
                table: "otp_codes",
                type: "varchar(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_device_id",
                table: "otp_codes",
                column: "device_id");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_target_purpose",
                table: "otp_codes",
                columns: new[] { "target", "purpose" });

            migrationBuilder.AddForeignKey(
                name: "FK_otp_codes_devices_device_id",
                table: "otp_codes",
                column: "device_id",
                principalTable: "devices",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_otp_codes_devices_device_id",
                table: "otp_codes");

            migrationBuilder.DropIndex(
                name: "IX_otp_codes_device_id",
                table: "otp_codes");

            migrationBuilder.DropIndex(
                name: "IX_otp_codes_target_purpose",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "device_id",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "purpose",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "target",
                table: "otp_codes");

            migrationBuilder.UpdateData(
                table: "profiles",
                keyColumn: "phone_number",
                keyValue: null,
                column: "phone_number",
                value: "");

            migrationBuilder.AlterColumn<string>(
                name: "phone_number",
                table: "profiles",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "varchar(32)",
                oldMaxLength: 32,
                oldNullable: true)
                .Annotation("MySql:CharSet", "utf8mb4")
                .OldAnnotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AlterColumn<Guid>(
                name: "profile_id",
                table: "otp_codes",
                type: "char(36)",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                collation: "ascii_general_ci",
                oldClrType: typeof(Guid),
                oldType: "char(36)",
                oldNullable: true)
                .OldAnnotation("Relational:Collation", "ascii_general_ci");
        }
    }
}
