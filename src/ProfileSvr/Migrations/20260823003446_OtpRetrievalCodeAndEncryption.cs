using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class OtpRetrievalCodeAndEncryption : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old hashed codes cannot be migrated to encrypted form and would collide
            // on the new unique retrieval_code index - purge them; they are all expired dev codes.
            migrationBuilder.Sql("DELETE FROM otp_codes;");

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

            migrationBuilder.AddColumn<string>(
                name: "retrieval_code",
                table: "otp_codes",
                type: "varchar(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_retrieval_code",
                table: "otp_codes",
                column: "retrieval_code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_otp_codes_retrieval_code",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "code_encrypted",
                table: "otp_codes");

            migrationBuilder.DropColumn(
                name: "retrieval_code",
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
    }
}
