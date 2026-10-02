using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableOccurrences : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "TriggerSequence",
                schema: "scheduling",
                table: "plans",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

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

            migrationBuilder.CreateIndex(
                name: "ux_occurrences_plan_sequence",
                schema: "scheduling",
                table: "occurrences",
                columns: new[] { "PlanId", "TriggerSequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "occurrences",
                schema: "scheduling");

            migrationBuilder.DropColumn(
                name: "TriggerSequence",
                schema: "scheduling",
                table: "plans");
        }
    }
}
