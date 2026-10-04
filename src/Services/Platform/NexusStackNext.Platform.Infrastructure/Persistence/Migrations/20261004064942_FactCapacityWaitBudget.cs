using Microsoft.EntityFrameworkCore.Migrations;

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

#nullable disable

namespace NexusStackNext.Platform.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "platform", "platform.setting-committed.v1", "account_setting_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "platform", "platform.setting-committed.v1", "account_setting_fact");
        }
    }
}
