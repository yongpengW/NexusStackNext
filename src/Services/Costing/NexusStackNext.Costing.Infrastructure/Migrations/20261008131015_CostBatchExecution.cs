using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CostBatchExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Attempts",
                schema: "costing",
                table: "batches",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableAt",
                schema: "costing",
                table: "batches",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)));

            migrationBuilder.AddColumn<long>(
                name: "Epoch",
                schema: "costing",
                table: "batches",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<string>(
                name: "ErrorCode",
                schema: "costing",
                table: "batches",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionOrigin",
                schema: "costing",
                table: "batches",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                schema: "costing",
                table: "batches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaxLeaseUntil",
                schema: "costing",
                table: "batches",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "batch_attempts",
                schema: "costing",
                columns: table => new
                {
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_batch_attempts", x => new { x.BatchId, x.Epoch });
                    table.ForeignKey(
                        name: "FK_batch_attempts_batches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "costing",
                        principalTable: "batches",
                        principalColumn: "BatchId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_batches_State_AvailableAt",
                schema: "costing",
                table: "batches",
                columns: new[] { "State", "AvailableAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "batch_attempts",
                schema: "costing");

            migrationBuilder.DropIndex(
                name: "IX_batches_State_AvailableAt",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "Attempts",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "Epoch",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "ErrorCode",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "ExecutionOrigin",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "costing",
                table: "batches");

            migrationBuilder.DropColumn(
                name: "MaxLeaseUntil",
                schema: "costing",
                table: "batches");
        }
    }
}
