using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddRegulatoryEntityProfileFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "EntityType",
                table: "Vendors",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "RegulatoryFactsJson",
                table: "Vendors",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<JsonDocument>(
                name: "RegulatoryFactsJson",
                table: "Organizations",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "State",
                table: "Organizations",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EntityType",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "RegulatoryFactsJson",
                table: "Vendors");

            migrationBuilder.DropColumn(
                name: "RegulatoryFactsJson",
                table: "Organizations");

            migrationBuilder.DropColumn(
                name: "State",
                table: "Organizations");
        }
    }
}
