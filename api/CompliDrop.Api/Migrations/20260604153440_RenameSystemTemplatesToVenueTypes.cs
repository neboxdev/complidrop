using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class RenameSystemTemplatesToVenueTypes : Migration
    {
        // Old generic trade name → new event-venue vendor type (#192). Kept in
        // lockstep with ComplianceTemplateSeed.Templates. The rename preserves each
        // template's Id (and any vendor FK) and keeps its existing rules; the
        // name-keyed seed (EnsureAsync) then sees the new names as already-present
        // and won't duplicate. UpdateData is parameterized (no raw SQL, no ADR 0009
        // concern) and a no-op on a fresh DB (0 system rows yet → seed creates them).
        private static readonly (string Old, string New)[] Renames =
        [
            ("General Sub Contractor", "Caterer"),
            ("Property Vendor", "Event Rental Company"),
            ("Healthcare Provider", "Security Service"),
            ("Transport Driver", "Transportation / Shuttle"),
            ("Professional Consultant", "Photographer / Videographer"),
        ];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldName, newName) in Renames)
                Rename(migrationBuilder, oldName, newName);

            // Drop the old generic "additional_insured = 'property'" rule the Property
            // Vendor template carried — it reads nonsensically once renamed/cloned ("Names
            // 'property' as additional insured"), and a venue names ITSELF, so the value is
            // per-tenant (the user adds it after cloning). additional_insured appears only in
            // this one seeded system checklist, so the IsSystemTemplate-scoped delete is
            // precise. Constant SQL, no timestamptz → ADR 0009 N/A. (#192 review.)
            migrationBuilder.Sql(
                "DELETE FROM \"ComplianceRules\" WHERE \"FieldName\" = 'additional_insured' " +
                "AND \"ComplianceTemplateId\" IN " +
                "(SELECT \"Id\" FROM \"ComplianceTemplates\" WHERE \"IsSystemTemplate\" = true);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            foreach (var (oldName, newName) in Renames)
                Rename(migrationBuilder, newName, oldName);
        }

        private static void Rename(MigrationBuilder migrationBuilder, string from, string to) =>
            migrationBuilder.UpdateData(
                table: "ComplianceTemplates",
                keyColumns: ["Name", "IsSystemTemplate"],
                keyValues: [from, true],
                column: "Name",
                value: to);
    }
}
