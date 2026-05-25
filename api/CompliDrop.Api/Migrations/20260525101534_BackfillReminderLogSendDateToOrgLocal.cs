using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// Recomputes <c>ReminderLogs.SendDate</c> from the UTC calendar day at send-time to the
    /// <em>org-local</em> calendar day. Aligns historical rows with the worker's new value
    /// assignment (see ADR 0007 + ticket #24).
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
    /// The <c>pg_timezone_names</c> guard filters out rows whose <c>TimeZone</c> string Postgres
    /// can't resolve, so a single bad value cannot abort the deploy. Today the only writer is
    /// <c>NormalizeTimeZone</c> in <c>AuthEndpoints</c> (validates via
    /// <c>TimeZoneInfo.FindSystemTimeZoneById</c>); the guard exists for any future writer
    /// (admin tool, seed script) that skips that helper. Skipped rows keep their pre-#24
    /// SendDate value and surface naturally the next time the worker writes for that org.
    /// </para>
    /// <para>
    /// MVP table is near-empty (a handful of rows at most), so a single UPDATE is fine. If this
    /// ever ships against a large table, batch in chunks of a few thousand rows.
    /// </para>
    /// </summary>
    public partial class BackfillReminderLogSendDateToOrgLocal : Migration
    {
        /// <summary>
        /// Backfill SQL — exposed as a <c>const</c> so the integration test
        /// <c>Backfill_sql_rewrites_legacy_utc_send_date_to_org_local_for_tokyo_row</c> can
        /// execute the exact statement the migration ships, with no copy-paste drift.
        /// </summary>
        internal const string UpSql = """
            UPDATE "ReminderLogs" AS l
            SET "SendDate" = ((l."SentAt" AT TIME ZONE o."TimeZone"))::date
            FROM "Reminders" AS r
            INNER JOIN "Organizations" AS o ON o."Id" = r."OrganizationId"
            WHERE r."Id" = l."ReminderId"
              AND o."TimeZone" IN (SELECT name FROM pg_timezone_names)
              AND ((l."SentAt" AT TIME ZONE o."TimeZone"))::date <> l."SendDate";
            """;

        /// <summary>
        /// Reverse: put SendDate back on the UTC calendar day of SentAt. Same WHERE guards
        /// against rewriting rows already on the UTC day (e.g. America/New_York firing at
        /// 13:00 UTC — SendDate equals Jan 15 either way). Symmetry only; we don't expect
        /// to run this in production.
        /// </summary>
        internal const string DownSql = """
            UPDATE "ReminderLogs" AS l
            SET "SendDate" = (l."SentAt" AT TIME ZONE 'UTC')::date
            WHERE (l."SentAt" AT TIME ZONE 'UTC')::date <> l."SendDate";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(DownSql);
        }
    }
}
