using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <summary>
    /// Data remediation for #273: nulls <c>Vendors.ComplianceTemplateId</c> wherever the
    /// referenced template is not assignable under the tenant rules — i.e. it is soft-deleted,
    /// or it belongs to another organization and is not a system template.
    ///
    /// <para>
    /// Two sources of such rows: (a) the normal assign-then-delete flow — <c>DeleteTemplate</c>
    /// historically soft-deleted the template without clearing vendor references, and the new
    /// assignment-time guard would otherwise 400 every edit-form save for those vendors; and
    /// (b) any cross-org assignment written while the FK was the only guard (the #273 hole) —
    /// the evaluation paths treat such a template as absent after this fix, but the stored
    /// reference itself should not survive.
    /// </para>
    /// <para>
    /// The predicate mirrors <c>VendorEndpoints.TemplateIsAssignable</c> / the AppDbContext
    /// query filter exactly: assignable = <c>DeletedAt IS NULL AND (IsSystemTemplate OR same
    /// org)</c>. Plain column comparisons only — no timestamptz expressions (ADR 0009 n/a).
    /// Vendors table is MVP-small, so a single UPDATE is fine.
    /// </para>
    /// <para>
    /// A second statement purges ComplianceCheck rows already LEAKED by a cross-org evaluation
    /// (rule's template org ≠ document org): the lazy self-heal in ComplianceCheckService cannot
    /// reach them for an EXPIRED document — the Expired early-return exits before the
    /// no-governing-rules branch — so they would otherwise stay visible via ListChecks forever.
    /// Checks referencing the org's OWN (soft-deleted) templates are deliberately NOT purged:
    /// no tenant boundary is crossed, and the next evaluation clears them where reachable.
    /// </para>
    /// </summary>
    public partial class ClearForeignOrDeletedVendorTemplateRefs : Migration
    {
        /// <summary>
        /// Exposed as a <c>const</c> so the integration test can execute the exact statement
        /// the migration ships, with no copy-paste drift (same pattern as
        /// <see cref="BackfillReminderLogSendDateToOrgLocal"/>).
        /// </summary>
        internal const string UpSql = """
            UPDATE "Vendors" AS v
            SET "ComplianceTemplateId" = NULL
            WHERE v."ComplianceTemplateId" IS NOT NULL
              AND NOT EXISTS (
                  SELECT 1 FROM "ComplianceTemplates" AS t
                  WHERE t."Id" = v."ComplianceTemplateId"
                    AND t."DeletedAt" IS NULL
                    AND (t."IsSystemTemplate" OR t."OrganizationId" = v."OrganizationId")
              );
            """;

        /// <summary>
        /// Purges check rows written by a cross-org evaluation. Exposed for the same
        /// test-pinning reason as <see cref="UpSql"/>.
        /// </summary>
        internal const string PurgeLeakedChecksSql = """
            DELETE FROM "ComplianceChecks" AS c
            USING "ComplianceRules" AS r, "ComplianceTemplates" AS t, "Documents" AS d
            WHERE c."ComplianceRuleId" = r."Id"
              AND r."ComplianceTemplateId" = t."Id"
              AND c."DocumentId" = d."Id"
              AND NOT t."IsSystemTemplate"
              AND t."OrganizationId" <> d."OrganizationId";
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(UpSql);
            migrationBuilder.Sql(PurgeLeakedChecksSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible data fix: the cleared references cannot be reconstructed. Down is a
            // deliberate no-op (rolling back the schema migration chain must not fail here).
        }
    }
}
