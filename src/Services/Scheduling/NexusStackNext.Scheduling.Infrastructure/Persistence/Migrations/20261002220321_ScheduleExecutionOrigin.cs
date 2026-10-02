using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ScheduleExecutionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ExecutionOrigin",
                schema: "scheduling",
                table: "plans",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionOrigin",
                schema: "scheduling",
                table: "occurrences",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ExecutionOrigin",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "ExecutionOrigin",
                schema: "scheduling",
                table: "occurrences");
        }
    }
}
