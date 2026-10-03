using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommittedFactExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "InitiatorId",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OperationId",
                schema: "auditing",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OperationSource",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RootOperationId",
                schema: "auditing",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RootSource",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "InitiatorId",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "OperationId",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "OperationSource",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "RootOperationId",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "RootSource",
                schema: "auditing",
                table: "audit_entries");
        }
    }
}
