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
