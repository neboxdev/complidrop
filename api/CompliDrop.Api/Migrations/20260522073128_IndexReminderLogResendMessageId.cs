using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class IndexReminderLogResendMessageId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ReminderLogs_ResendMessageId",
                table: "ReminderLogs",
                column: "ResendMessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReminderLogs_ResendMessageId",
                table: "ReminderLogs");
        }
    }
}
