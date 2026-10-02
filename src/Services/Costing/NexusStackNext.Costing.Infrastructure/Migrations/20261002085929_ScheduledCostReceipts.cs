using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScheduledCostReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                schema: "costing",
                table: "tasks",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                defaultValue: "manual");

            migrationBuilder.CreateTable(
                name: "schedule_receipts",
                schema: "costing",
                columns: table => new
                {
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerSequence = table.Column<long>(type: "bigint", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Decision = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_schedule_receipts", x => x.OccurrenceId);
                    table.ForeignKey(
                        name: "FK_schedule_receipts_tasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "costing",
                        principalTable: "tasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_schedule_receipts_TaskId",
                schema: "costing",
                table: "schedule_receipts",
                column: "TaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "schedule_receipts",
                schema: "costing");

            migrationBuilder.DropColumn(
                name: "Origin",
                schema: "costing",
                table: "tasks");
        }
    }
}
