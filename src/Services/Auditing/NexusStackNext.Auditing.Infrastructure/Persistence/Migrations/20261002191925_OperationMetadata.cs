using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationMetadata : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Action",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CorrelationId",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Description",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExecutionRole",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentSpanId",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpanId",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectId",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(36)",
                maxLength: 36,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectIdKind",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SubjectType",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Action",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "CorrelationId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "Description",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "ExecutionRole",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "ParentSpanId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "SpanId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "SubjectId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "SubjectIdKind",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "SubjectType",
                schema: "auditing",
                table: "operation_observations");
        }
    }
}
