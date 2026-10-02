using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationExecutionCorrelation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitiatorId",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ParentOperationId",
                schema: "auditing",
                table: "operation_observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ParentSource",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RootOperationId",
                schema: "auditing",
                table: "operation_observations",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RootSource",
                schema: "auditing",
                table: "operation_observations",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "TaskEpoch",
                schema: "auditing",
                table: "operation_observations",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TaskId",
                schema: "auditing",
                table: "operation_observations",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatorId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "ParentOperationId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "ParentSource",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "RootOperationId",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "RootSource",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "TaskEpoch",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropColumn(
                name: "TaskId",
                schema: "auditing",
                table: "operation_observations");
        }
    }
}
