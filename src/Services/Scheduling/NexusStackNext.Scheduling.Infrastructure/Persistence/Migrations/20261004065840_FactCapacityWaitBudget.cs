using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "scheduling", "scheduling.plan-committed.v1", "account_plan_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "scheduling", "scheduling.plan-committed.v1", "account_plan_fact");
        }
    }
}
