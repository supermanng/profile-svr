using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddOtpCodesTable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "otp_codes",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    profile_id = table.Column<Guid>(type: "char(36)", nullable: false, collation: "ascii_general_ci"),
                    channel = table.Column<string>(type: "varchar(16)", maxLength: 16, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    code_hash = table.Column<string>(type: "varchar(64)", maxLength: 64, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    expires_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    consumed_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_at_utc = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_otp_codes", x => x.id);
                    table.ForeignKey(
                        name: "FK_otp_codes_profiles_profile_id",
                        column: x => x.profile_id,
                        principalTable: "profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_profile_id_channel",
                table: "otp_codes",
                columns: new[] { "profile_id", "channel" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "otp_codes");
        }
    }
}
