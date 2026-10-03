using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.OperationJournal.Migrations
{
    /// <inheritdoc />
    public partial class JournalRecoveryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recovery_records_RecoveredAt_RequestId",
                schema: "operation_journal",
                table: "recovery_records");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RetainUntil",
                schema: "operation_journal",
                table: "recovery_records",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE operation_journal.recovery_records
                SET "RetainUntil" = "RecoveredAt" + interval '30 days';
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "RetainUntil",
                schema: "operation_journal",
                table: "recovery_records",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_recovery_records_RetainUntil_RequestId",
                schema: "operation_journal",
                table: "recovery_records",
                columns: new[] { "RetainUntil", "RequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recovery_records_RetainUntil_RequestId",
                schema: "operation_journal",
                table: "recovery_records");

            migrationBuilder.DropColumn(
                name: "RetainUntil",
                schema: "operation_journal",
                table: "recovery_records");

            migrationBuilder.CreateIndex(
                name: "IX_recovery_records_RecoveredAt_RequestId",
                schema: "operation_journal",
                table: "recovery_records",
                columns: new[] { "RecoveredAt", "RequestId" });
        }
    }
}
