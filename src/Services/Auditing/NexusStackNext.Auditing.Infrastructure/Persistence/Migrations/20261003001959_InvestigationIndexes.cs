using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InvestigationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_operation_observations_root",
                schema: "auditing",
                table: "operation_observations",
                columns: new[] { "RootOperationId", "RootSource" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_observations_subject",
                schema: "auditing",
                table: "operation_observations",
                columns: new[] { "SubjectType", "SubjectId" });

            migrationBuilder.CreateIndex(
                name: "ix_operation_observations_task",
                schema: "auditing",
                table: "operation_observations",
                columns: new[] { "TaskId", "TaskEpoch" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_actor",
                schema: "auditing",
                table: "audit_entries",
                columns: new[] { "ActorId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_occurred",
                schema: "auditing",
                table: "audit_entries",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_operation",
                schema: "auditing",
                table: "audit_entries",
                columns: new[] { "OperationId", "OperationSource" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_root",
                schema: "auditing",
                table: "audit_entries",
                columns: new[] { "RootOperationId", "RootSource" });

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_subject",
                schema: "auditing",
                table: "audit_entries",
                columns: new[] { "SubjectType", "SubjectId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_operation_observations_root",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropIndex(
                name: "ix_operation_observations_subject",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropIndex(
                name: "ix_operation_observations_task",
                schema: "auditing",
                table: "operation_observations");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_actor",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_occurred",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_operation",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_root",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropIndex(
                name: "ix_audit_entries_subject",
                schema: "auditing",
                table: "audit_entries");
        }
    }
}
