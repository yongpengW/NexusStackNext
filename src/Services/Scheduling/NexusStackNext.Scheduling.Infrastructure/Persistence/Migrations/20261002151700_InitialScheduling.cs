using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialScheduling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "scheduling");

            migrationBuilder.CreateTable(
                name: "decisions",
                schema: "scheduling",
                columns: table => new
                {
                    DecisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    PlanVersion = table.Column<long>(type: "bigint", nullable: false),
                    ScheduleRevision = table.Column<long>(type: "bigint", nullable: false),
                    Rule = table.Column<string>(type: "jsonb", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_decisions", x => x.DecisionId);
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "scheduling",
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
                name: "occurrences",
                schema: "scheduling",
                columns: table => new
                {
                    OccurrenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<long>(type: "bigint", nullable: false),
                    TriggerSequence = table.Column<long>(type: "bigint", nullable: false),
                    ScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TriggeredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_occurrences", x => x.OccurrenceId);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "scheduling",
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
                name: "plans",
                schema: "scheduling",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetId = table.Column<Guid>(type: "uuid", nullable: false),
                    DelegatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RuleKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Expression = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TimeZoneId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CronFieldCount = table.Column<int>(type: "integer", nullable: true),
                    Day = table.Column<int>(type: "integer", nullable: true),
                    Hour = table.Column<int>(type: "integer", nullable: true),
                    Minute = table.Column<int>(type: "integer", nullable: true),
                    MisfirePolicy = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    GraceSeconds = table.Column<double>(type: "double precision", nullable: true),
                    Interval = table.Column<TimeSpan>(type: "interval", nullable: true),
                    ScheduleRevision = table.Column<long>(type: "bigint", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    TriggerSequence = table.Column<long>(type: "bigint", nullable: false),
                    RetryAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSchedulingErrorCode = table.Column<string>(type: "character varying(96)", maxLength: 96, nullable: true),
                    SchedulingFailureCount = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_decisions_plan_version",
                schema: "scheduling",
                table: "decisions",
                columns: new[] { "PlanId", "PlanVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_occurrences_plan_sequence",
                schema: "scheduling",
                table: "occurrences",
                columns: new[] { "PlanId", "TriggerSequence" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "scheduling",
                table: "outbox",
                columns: new[] { "DeliveredAt", "DeadLetteredAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "ix_plans_due",
                schema: "scheduling",
                table: "plans",
                columns: new[] { "IsEnabled", "NextRunAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "ux_plans_code",
                schema: "scheduling",
                table: "plans",
                column: "Code",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "decisions",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "occurrences",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "scheduling");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "scheduling");
        }
    }
}
