using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ProfileSvr.Migrations
{
    /// <inheritdoc />
    public partial class AddProfileStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "profiles",
                type: "varchar(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "AuthCreated")
                .Annotation("MySql:CharSet", "utf8mb4");

            // Profiles that already completed onboarding stay usable.
            migrationBuilder.Sql("UPDATE profiles SET status = 'Active' WHERE first_name IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "status",
                table: "profiles");
        }
    }
}
