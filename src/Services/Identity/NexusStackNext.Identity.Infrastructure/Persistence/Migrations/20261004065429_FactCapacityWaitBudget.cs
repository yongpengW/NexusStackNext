using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "identity", "identity.entity-committed.v1", "account_identity_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "identity", "identity.entity-committed.v1", "account_identity_fact");
        }
    }
}
