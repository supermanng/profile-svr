using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddVirtualAccountsAndCredits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "virtual_account",
                table: "profiles",
                type: "varchar(50)",
                maxLength: 50,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "virtual_account_bank",
                table: "profiles",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "virtual_account_credits",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    transaction_ref = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    virtual_account = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    amount = table.Column<decimal>(type: "decimal(18,2)", precision: 18, scale: 2, nullable: false),
                    source_account = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    source_bank = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sender_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    narration = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    transaction_date = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    status = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    credit_posted = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    posting_response = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    posting_abandoned = table.Column<bool>(type: "tinyint(1)", nullable: false, defaultValue: false),
                    last_error = table.Column<string>(type: "varchar(1000)", maxLength: 1000, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_account_credits", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "virtual_account_mappings",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    profile_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    virtual_account = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    cba_account = table.Column<string>(type: "varchar(50)", maxLength: 50, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    account_name = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    bank = table.Column<string>(type: "varchar(150)", maxLength: 150, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_virtual_account_mappings", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_virtual_account_credits_credit_posted_posting_abandoned_stat~",
                table: "virtual_account_credits",
                columns: new[] { "credit_posted", "posting_abandoned", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_virtual_account_credits_transaction_date",
                table: "virtual_account_credits",
                column: "transaction_date");

            migrationBuilder.CreateIndex(
                name: "IX_virtual_account_credits_transaction_ref",
                table: "virtual_account_credits",
                column: "transaction_ref",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_virtual_account_mappings_profile_id",
                table: "virtual_account_mappings",
                column: "profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_virtual_account_mappings_virtual_account",
                table: "virtual_account_mappings",
                column: "virtual_account",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "virtual_account_credits");

            migrationBuilder.DropTable(
                name: "virtual_account_mappings");

            migrationBuilder.DropColumn(
                name: "virtual_account",
                table: "profiles");

            migrationBuilder.DropColumn(
                name: "virtual_account_bank",
                table: "profiles");
        }
    }
}
