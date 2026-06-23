using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddDocumentSupersessionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_VendorId",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_Supersession",
                table: "Documents",
                columns: new[] { "VendorId", "DocumentType", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Documents_Supersession",
                table: "Documents");

            migrationBuilder.CreateIndex(
                name: "IX_Documents_VendorId",
                table: "Documents",
                column: "VendorId");
        }
    }
}
