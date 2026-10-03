using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class TaskManagement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CreatedAt",
                schema: "costing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("ALTER TABLE costing.tasks ALTER COLUMN \"CreatedAt\" SET DEFAULT clock_timestamp();");

            migrationBuilder.AddColumn<int>(
                name: "DelaySeconds",
                schema: "costing",
                table: "tasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MaxLeaseUntil",
                schema: "costing",
                table: "tasks",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "costing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "DelaySeconds",
                schema: "costing",
                table: "tasks");

            migrationBuilder.DropColumn(
                name: "MaxLeaseUntil",
                schema: "costing",
                table: "tasks");
        }
    }
}
