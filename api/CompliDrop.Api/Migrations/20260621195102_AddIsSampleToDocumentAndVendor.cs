using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddIsSampleToDocumentAndVendor : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSample",
                table: "Vendors",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsSample",
                table: "Documents",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateIndex(
                name: "IX_Documents_OrganizationId_SampleUnique",
                table: "Documents",
                column: "OrganizationId",
                unique: true,
                filter: "\"IsSample\" AND \"DeletedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_OrganizationId_SampleUnique",
                table: "Documents");

            migrationBuilder.DropColumn(
                name: "IsSample",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "IsSample",
                table: "Documents");
        }
    }
}
