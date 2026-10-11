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
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '5s';
                LOCK TABLE pricing.exports, pricing.export_publications IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM pricing.exports) OR EXISTS (SELECT 1 FROM pricing.export_publications) THEN
                    RAISE EXCEPTION 'pricing_exports_history_retained';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "Execution",
                schema: "pricing",
                table: "webhook_deliveries");
        }
    }
}
