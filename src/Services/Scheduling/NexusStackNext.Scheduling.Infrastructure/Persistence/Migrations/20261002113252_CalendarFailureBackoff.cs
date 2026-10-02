using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarFailureBackoff : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastSchedulingErrorCode",
                schema: "scheduling",
                table: "plans",
                type: "character varying(96)",
                maxLength: 96,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetryAt",
                schema: "scheduling",
                table: "plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SchedulingFailureCount",
                schema: "scheduling",
                table: "plans",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSchedulingErrorCode",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "RetryAt",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "SchedulingFailureCount",
                schema: "scheduling",
                table: "plans");
        }
    }
}
