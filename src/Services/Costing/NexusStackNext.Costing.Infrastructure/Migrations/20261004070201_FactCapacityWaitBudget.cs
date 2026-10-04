using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "costing", "costing.cost-sheet-committed.v1", "account_cost_sheet_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "costing", "costing.cost-sheet-committed.v1", "account_cost_sheet_fact");
        }
    }
}
