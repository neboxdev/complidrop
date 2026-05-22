using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class WidenReminderLogDedupeKeyWithRecipient : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReminderLogs_ReminderId_DocumentId_SendDate",
                table: "ReminderLogs");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLogs_ReminderId_DocumentId_SendDate_RecipientEmail",
                table: "ReminderLogs",
                columns: new[] { "ReminderId", "DocumentId", "SendDate", "RecipientEmail" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReminderLogs_ReminderId_DocumentId_SendDate_RecipientEmail",
                table: "ReminderLogs");

            migrationBuilder.CreateIndex(
                name: "IX_ReminderLogs_ReminderId_DocumentId_SendDate",
                table: "ReminderLogs",
                columns: new[] { "ReminderId", "DocumentId", "SendDate" },
                unique: true);
        }
    }
}
