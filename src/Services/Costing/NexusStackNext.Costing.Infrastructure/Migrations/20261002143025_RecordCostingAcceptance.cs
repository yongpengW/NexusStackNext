using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RecordCostingAcceptance : Migration
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

            // 旧行保持未知；默认值只用于迁移完成后的新接受。
            migrationBuilder.Sql("ALTER TABLE costing.tasks ALTER COLUMN \"CreatedAt\" SET DEFAULT clock_timestamp()");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CreatedAt",
                schema: "costing",
                table: "tasks");
        }
    }
}
