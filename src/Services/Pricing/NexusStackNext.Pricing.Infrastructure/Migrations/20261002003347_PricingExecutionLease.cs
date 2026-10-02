using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PricingExecutionLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AvailableAt",
                schema: "pricing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: false,
                defaultValueSql: "clock_timestamp()");

            migrationBuilder.AddColumn<long>(
                name: "Epoch",
                schema: "pricing",
                table: "tasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LeaseUntil",
                schema: "pricing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_tasks_State_AvailableAt",
                schema: "pricing",
                table: "tasks",
                columns: new[] { "State", "AvailableAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tasks_State_AvailableAt",
                schema: "pricing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "AvailableAt",
                schema: "pricing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "Epoch",
                schema: "pricing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                schema: "pricing",
                table: "tasks");
        }
    }
}
