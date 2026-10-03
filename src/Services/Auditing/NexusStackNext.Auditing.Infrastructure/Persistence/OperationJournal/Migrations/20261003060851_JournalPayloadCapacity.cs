using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.OperationJournal.Migrations
{
    /// <inheritdoc />
    public partial class JournalPayloadCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_capacity_valid",
                schema: "operation_journal",
                table: "capacity");

            migrationBuilder.AddColumn<long>(
                name: "PayloadBytes",
                schema: "operation_journal",
                table: "capacity",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
            migrationBuilder.Sql("""
                UPDATE operation_journal.capacity
                SET "PayloadBytes" = (SELECT coalesce(sum(octet_length("Payload")), 0) FROM operation_journal.outbox)
                WHERE "Id" = 1;
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_capacity_valid",
                schema: "operation_journal",
                table: "capacity",
                sql: "\"Id\" = 1 AND \"RecordCount\" >= 0 AND \"PayloadBytes\" >= 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_capacity_valid",
                schema: "operation_journal",
                table: "capacity");

            migrationBuilder.DropColumn(
                name: "PayloadBytes",
                schema: "operation_journal",
                table: "capacity");

            migrationBuilder.AddCheckConstraint(
                name: "ck_capacity_valid",
                schema: "operation_journal",
                table: "capacity",
                sql: "\"Id\" = 1 AND \"RecordCount\" >= 0");
        }
    }
}
