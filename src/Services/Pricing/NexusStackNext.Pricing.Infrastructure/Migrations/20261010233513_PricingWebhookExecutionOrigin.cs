using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PricingWebhookExecutionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Execution",
                schema: "pricing",
                table: "webhook_deliveries",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Execution",
                schema: "pricing",
                table: "webhook_deliveries");
        }
    }
}
