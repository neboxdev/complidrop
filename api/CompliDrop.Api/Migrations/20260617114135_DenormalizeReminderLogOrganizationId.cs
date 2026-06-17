using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// Denormalizes <c>Reminder.OrganizationId</c> onto <c>ReminderLog</c> (#309) and adds the
    /// composite <c>(OrganizationId, SentAt DESC)</c> index that backs the org-scoped reminder
    /// history read (<c>GET /api/reminders/history</c>). Pre-#309 that read scanned the whole
    /// table, sorted the top 200, and semi-joined <c>Reminders</c> for org scoping (no org column
    /// existed on the log); with the column + index it is an index range scan.
    ///
    /// <para>
    /// The column is added <em>nullable</em>, backfilled from each row's parent
    /// <c>Reminder</c>, then tightened to <c>NOT NULL</c> — so existing rows get a real org id
    /// and the final schema carries no lingering DB default (a future insert that forgets the org
    /// fails loudly rather than silently writing the empty guid). The FK
    /// <c>ReminderLog.ReminderId → Reminders.Id</c> (ON DELETE CASCADE) guarantees every log has a
    /// live parent, so the join matches every row and the <c>SET NOT NULL</c> can't fail on a
    /// straggler; were an orphan ever to exist, the failure is the right outcome (it surfaces the
    /// corruption instead of masking it with the empty guid).
    /// </para>
    /// <para>
    /// No <c>timestamptz</c> arithmetic here, so ADR 0009 doesn't apply — the backfill is a pure
    /// id copy across the parent join.
    /// </para>
    /// <para>
    /// MVP table is near-empty (a handful of rows at most), so a single UPDATE is fine. If this
    /// ever ships against a large table, batch in chunks of a few thousand rows.
    /// </para>
    /// </summary>
    public partial class DenormalizeReminderLogOrganizationId : Migration
    {
        /// <summary>
        /// Backfill SQL — exposed as a <c>const</c> so the integration test
        /// <c>Backfill_sql_copies_organization_id_from_parent_reminder</c> executes the exact
        /// statement the migration ships, with no copy-paste drift (same pattern as
        /// <see cref="BackfillReminderLogSendDateToOrgLocal.UpSql"/>).
        /// </summary>
        internal const string BackfillSql = """
            UPDATE "ReminderLogs" AS l
            SET "OrganizationId" = r."OrganizationId"
            FROM "Reminders" AS r
            WHERE r."Id" = l."ReminderId";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Add nullable so existing rows are admitted without a fabricated default value.
            migrationBuilder.AddColumn<Guid>(
                name: "OrganizationId",
                table: "ReminderLogs",
                type: "uuid",
                nullable: true);

            // 2. Backfill every existing row from its parent Reminder's org.
            migrationBuilder.Sql(BackfillSql);

            // 3. Tighten to NOT NULL — no DB default left behind (see class remarks).
            migrationBuilder.AlterColumn<Guid>(
                name: "OrganizationId",
                table: "ReminderLogs",
                type: "uuid",
                nullable: false,
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            // 4. Index last, so the bulk backfill UPDATE doesn't pay per-row index maintenance.
            migrationBuilder.CreateIndex(
                name: "IX_ReminderLogs_OrganizationId_SentAt",
                table: "ReminderLogs",
                columns: new[] { "OrganizationId", "SentAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ReminderLogs_OrganizationId_SentAt",
                table: "ReminderLogs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "ReminderLogs");
        }
    }
}
