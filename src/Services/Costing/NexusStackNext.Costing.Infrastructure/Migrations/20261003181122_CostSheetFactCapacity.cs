using Microsoft.EntityFrameworkCore.Migrations;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CostSheetFactCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact_capacity",
                schema: "costing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RetainedRecords = table.Column<long>(type: "bigint", nullable: false),
                    RetainedPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecords = table.Column<long>(type: "bigint", nullable: false),
                    MaxPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecordPayloadBytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_capacity", x => x.Id);
                    table.CheckConstraint("ck_fact_capacity_bounds", "\"RetainedRecords\" >= 0 AND \"RetainedPayloadBytes\" >= 0 AND \"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\"");
                    table.CheckConstraint("ck_fact_capacity_singleton", "\"Id\" = 1");
                });
            CommittedFactCapacityMigrationV1.Initialize(migrationBuilder, "costing", "costing.cost-sheet-committed.v1");
            CommittedFactCapacityMigrationV1.Install(migrationBuilder, "costing", "costing.cost-sheet-committed.v1", "account_cost_sheet_fact");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            CommittedFactCapacityMigrationV1.Remove(migrationBuilder, "costing", "account_cost_sheet_fact");
            migrationBuilder.DropTable(
                name: "fact_capacity",
                schema: "costing");
        }
    }
}
