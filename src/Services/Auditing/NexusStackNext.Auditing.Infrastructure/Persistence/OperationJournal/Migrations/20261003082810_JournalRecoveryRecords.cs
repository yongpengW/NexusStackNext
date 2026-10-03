using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.OperationJournal.Migrations
{
    /// <inheritdoc />
    public partial class JournalRecoveryRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recovery_records",
                schema: "operation_journal",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PreviousRetryRevision = table.Column<long>(type: "bigint", nullable: false),
                    RetryRevision = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Account = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Machine = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecoveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recovery_records", x => x.RequestId);
                    table.CheckConstraint("ck_recovery_revision", "\"PreviousRetryRevision\" >= 0 AND \"RetryRevision\" = \"PreviousRetryRevision\" + 1");
                });

            migrationBuilder.CreateIndex(
                name: "IX_recovery_records_RecoveredAt_RequestId",
                schema: "operation_journal",
                table: "recovery_records",
                columns: new[] { "RecoveredAt", "RequestId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recovery_records",
                schema: "operation_journal");
        }
    }
}
