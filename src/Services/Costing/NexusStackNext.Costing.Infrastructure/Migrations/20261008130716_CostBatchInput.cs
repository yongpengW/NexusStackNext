using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CostBatchInput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "batches",
                schema: "costing",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    TotalRows = table.Column<int>(type: "integer", nullable: false),
                    Checkpoint = table.Column<int>(type: "integer", nullable: false),
                    Imported = table.Column<int>(type: "integer", nullable: false),
                    Unchanged = table.Column<int>(type: "integer", nullable: false),
                    DuplicateSuperseded = table.Column<int>(type: "integer", nullable: false),
                    Rejected = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batches", x => x.BatchId);
                });

            migrationBuilder.CreateTable(
                name: "batch_rows",
                schema: "costing",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    SourceRow = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FreightCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    EffectiveSequence = table.Column<int>(type: "integer", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_rows", x => new { x.BatchId, x.Sequence });
                    table.ForeignKey(
                        name: "FK_batch_rows_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "costing",
                        principalTable: "batches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_batch_rows_TaskId",
                schema: "costing",
                table: "batch_rows",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_batches_CreatedAt_BatchId",
                schema: "costing",
                table: "batches",
                columns: new[] { "CreatedAt", "BatchId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch_rows",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "batches",
                schema: "costing");
        }
    }
}
