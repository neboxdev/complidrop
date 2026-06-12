using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace CompliDrop.Api.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionLastStripeEventAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastStripeEventAt",
                table: "Subscriptions",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastStripeEventAt",
                table: "Subscriptions");
        }
    }
}
