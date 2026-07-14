using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class SeedRegradeRevisionWatermark : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "RegradedThroughRevision",
                table: "ComplianceTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "RulesRevision",
                table: "ComplianceTemplates",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RegradedThroughRevision",
                table: "ComplianceTemplates");

            migrationBuilder.DropColumn(
                name: "RulesRevision",
                table: "ComplianceTemplates");
        }
    }
}
