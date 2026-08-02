using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// #459 / ADR 0052 — gives extraction TRUST its own column so it stops sharing one with pipeline
    /// POSITION, and seeds it for rows that predate the column.
    /// <para/>
    /// ADDITIVE ONLY, and deliberately so: migrations auto-apply at boot and fail fast (ADR 0016), and a
    /// Railway deploy overlaps the old container with the new one. This adds a column with a store default
    /// (so an old instance's INSERT, which does not know the column, still lands a readable value) and then
    /// writes ONLY that brand-new column, from two values already on the same row. Nothing pre-existing is
    /// dropped, narrowed or mutated.
    /// </summary>
    public partial class AddDocumentExtractionTrust : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExtractionTrust",
                table: "Documents",
                type: "character varying(50)",
                maxLength: 50,
                nullable: false,
                defaultValue: "Trusted");

            // The seed for EXISTING rows is a decision, not a default (ADR 0052 § "The existing-row seed").
            // Leaving every pre-existing row at 'Trusted' would silently RE-COVER every document
            // VendorEndpoints.ComputeCoverage excludes today, which is the one regression this change may
            // not cause; seeding every row 'Distrusted' would drop every currently-covered vendor to
            // ActionNeeded overnight, with no route back short of re-extracting the whole corpus (re-paying
            // Document AI + the LLM per document) or hand-confirming each one.
            //
            // So the seed REPRODUCES the pre-#459 read-time predicate exactly — the two clauses
            // ComputeCoverage carried before this change — turning it into stored state:
            //   (a) ManualRequired: distrusted whatever IsManuallyVerified says. ResolveManualReview can
            //       set that flag and then RE-RAISE the review (ADR 0040's unreadable-value escalation), and
            //       such a row is excluded today, so the flag must not rescue it here either.
            //   (b) Failed AND NOT IsManuallyVerified: Amendment 2's clause verbatim, including its exit —
            //       a failed extraction a human already confirmed keeps its coverage across the migration.
            // Every other row (Completed / Pending / Processing / confirmed-Failed) keeps the column
            // default, matching the population that is in-force-eligible today.
            //
            // Written as one statement over a table in the low thousands of rows (reviewers.md § Scale), so
            // it is well inside the boot-migration budget ADR 0016 § Negative flags. The enum is stored as
            // text (HasConversion<string>), hence the string literals.
            migrationBuilder.Sql("""
                UPDATE "Documents"
                SET "ExtractionTrust" = 'Distrusted'
                WHERE "ExtractionStatus" = 'ManualRequired'
                   OR ("ExtractionStatus" = 'Failed' AND NOT "IsManuallyVerified");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The backfill needs no Down of its own: dropping the column drops everything it wrote.
            migrationBuilder.DropColumn(
                name: "ExtractionTrust",
                table: "Documents");
        }
    }
}
