using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddKycStatusAndBankAccounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "cad_account",
                table: "profiles",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "kyc_status",
                table: "profiles",
                type: "varchar(40)",
                maxLength: 40,
                nullable: false,
                defaultValue: "NotStarted")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "kyc_status_reason",
                table: "profiles",
                type: "varchar(500)",
                maxLength: 500,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "naira_account",
                table: "profiles",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "cad_account",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "kyc_status",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "kyc_status_reason",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "naira_account",
                table: "profiles");
        }
    }
}
