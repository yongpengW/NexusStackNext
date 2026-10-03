using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class SharedFactCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "identity", "identity.entity-committed.v1", "account_identity_fact");

        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // V1 与上一迁移的触发器语义完全相同；保留原表、策略和账本即可回退模型类型。

        }
    }
}
