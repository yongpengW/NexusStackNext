using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "pricing", "pricing.price-quote-committed.v1", "account_price_quote_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "pricing", "pricing.price-quote-committed.v1", "account_price_quote_fact");
        }
    }
}
