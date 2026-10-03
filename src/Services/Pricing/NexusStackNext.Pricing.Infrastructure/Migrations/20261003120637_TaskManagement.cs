using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "pricing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("ALTER TABLE pricing.tasks ALTER COLUMN \"CreatedAt\" SET DEFAULT clock_timestamp();");

            migrationBuilder.AddColumn<int>(
                name: "DelaySeconds",
                schema: "pricing",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaxLeaseUntil",
                schema: "pricing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "pricing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "DelaySeconds",
                schema: "pricing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "MaxLeaseUntil",
                schema: "pricing",
                table: "tasks");
        }
    }
}
