using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleExecutionCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ScheduleDecisionId",
                schema: "auditing",
                table: "operation_observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ScheduleExpectedVersion",
                schema: "auditing",
                table: "operation_observations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "SchedulePlanId",
                schema: "auditing",
                table: "operation_observations",
                type: "bigint",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ScheduleDecisionId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "ScheduleExpectedVersion",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "SchedulePlanId",
                schema: "auditing",
                table: "operation_observations");
        }
    }
}
