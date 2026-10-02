using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "costing");

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "costing",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.ConsumerName, x.EventName, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "costing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailure = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sheets",
                schema: "costing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchaseCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FreightCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    InputRevision = table.Column<long>(type: "bigint", nullable: false),
                    CalculatedRevision = table.Column<long>(type: "bigint", nullable: false),
                    UnitCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sheets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "tasks",
                schema: "costing",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedVersion = table.Column<long>(type: "bigint", nullable: false),
                    PurchaseCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FreightCost = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    InputRevision = table.Column<long>(type: "bigint", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "clock_timestamp()"),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tasks", x => x.TaskId);
                    table.ForeignKey(
                        name: "FK_tasks_sheets_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "costing",
                        principalTable: "sheets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attempts",
                schema: "costing",
                columns: table => new
                {
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Outcome = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_attempts", x => new { x.TaskId, x.Epoch });
                    table.ForeignKey(
                        name: "FK_attempts_tasks_TaskId",
                        column: x => x.TaskId,
                        principalSchema: "costing",
                        principalTable: "tasks",
                        principalColumn: "TaskId",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "costing",
                table: "outbox",
                columns: new[] { "DeliveredAt", "DeadLetteredAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "IX_tasks_ItemId",
                schema: "costing",
                table: "tasks",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_tasks_State_AvailableAt",
                schema: "costing",
                table: "tasks",
                columns: new[] { "State", "AvailableAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "attempts",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "tasks",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "sheets",
                schema: "costing");
        }
    }
}
