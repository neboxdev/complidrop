using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddUserHasCompletedOnboarding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasCompletedOnboarding",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Grandfather every user that existed BEFORE #191 to "already onboarded" —
            // they're oriented, so the first-run welcome must never retroactively fire
            // for them. New signups insert false (the column default above) and so still
            // see onboarding. (No timestamptz touched → ADR 0009 N/A.)
            migrationBuilder.Sql("UPDATE \"Users\" SET \"HasCompletedOnboarding\" = true;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasCompletedOnboarding",
                table: "Users");
        }
    }
}
