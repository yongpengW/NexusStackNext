using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

namespace NexusStackNext.Files.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FactCapacityWaitBudget : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV2.Install(migrationBuilder, "files", "files.stored-file-committed.v1", "account_file_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "files", "files.stored-file-committed.v1", "account_file_fact");
        }
    }
}
