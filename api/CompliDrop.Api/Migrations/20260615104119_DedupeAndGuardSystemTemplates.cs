using CompliDrop.Api.Data;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class DedupeAndGuardSystemTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-time cleanup of the duplicated system templates FIRST (#251), so the partial unique
            // index below can be created without tripping over the existing prod duplicates. No-op on
            // a fresh DB (no duplicates), so this is safe on every environment.
            migrationBuilder.Sql(SystemTemplateDedup.DedupeSql);

            migrationBuilder.CreateIndex(
                name: "IX_ComplianceTemplates_Name_SystemUnique",
                table: "ComplianceTemplates",
                column: "Name",
                unique: true,
                filter: "\"IsSystemTemplate\" AND \"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ComplianceTemplates_Name_SystemUnique",
                table: "ComplianceTemplates");
        }
    }
}
