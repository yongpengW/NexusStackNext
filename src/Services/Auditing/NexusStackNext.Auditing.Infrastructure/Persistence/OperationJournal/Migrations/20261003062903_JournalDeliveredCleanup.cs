using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.OperationJournal.Migrations
{
    /// <inheritdoc />
    public partial class JournalDeliveredCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_delivered_cleanup",
                schema: "operation_journal",
                table: "outbox",
                columns: new[] { "DeliveredAt", "Id" },
                filter: "\"DeliveredAt\" IS NOT NULL AND \"DeadLetteredAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_delivered_cleanup",
                schema: "operation_journal",
                table: "outbox");
        }
    }
}
