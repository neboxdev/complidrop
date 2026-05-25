using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// Recomputes <c>ReminderLogs.SendDate</c> from the UTC calendar day at send-time to the
    /// <em>org-local</em> calendar day. Aligns historical rows with the worker's new value
    /// assignment (see ADR 0002 amendment + ticket #24).
    ///
    /// <para>
    /// SentAt is <c>timestamptz</c> so <c>SentAt AT TIME ZONE org."TimeZone"</c> resolves the
    /// instant in the org's wall clock; <c>::date</c> truncates to the calendar day. The join
    /// goes ReminderLog → Reminder → Organization, since ReminderLog has no direct org column.
    /// </para>
    /// <para>
    /// Safe wrt the unique index <c>(ReminderId, DocumentId, SendDate, RecipientEmail)</c>: the
    /// worker only fires once per org-local day, so any row's SendDate shifts by at most one
    /// calendar day and can never collide with another row sharing the rest of the tuple.
    /// </para>
    /// <para>
    /// MVP table is near-empty (a handful of rows at most), so a single UPDATE is fine. If this
    /// ever ships against a large table, batch in chunks of a few thousand rows.
    /// </para>
    /// </summary>
    public partial class BackfillReminderLogSendDateToOrgLocal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                UPDATE "ReminderLogs" AS l
                SET "SendDate" = ((l."SentAt" AT TIME ZONE o."TimeZone"))::date
                FROM "Reminders" AS r
                INNER JOIN "Organizations" AS o ON o."Id" = r."OrganizationId"
                WHERE r."Id" = l."ReminderId"
                  AND ((l."SentAt" AT TIME ZONE o."TimeZone"))::date <> l."SendDate";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Reverse: put SendDate back on the UTC calendar day of SentAt. Same WHERE guards
            // against rewriting rows that are already on the UTC day (e.g. orgs in America/New_York
            // firing at 13:00 UTC — SendDate would equal Jan 15 either way).
            migrationBuilder.Sql("""
                UPDATE "ReminderLogs" AS l
                SET "SendDate" = (l."SentAt" AT TIME ZONE 'UTC')::date
                WHERE (l."SentAt" AT TIME ZONE 'UTC')::date <> l."SendDate";
                """);
        }
    }
}
