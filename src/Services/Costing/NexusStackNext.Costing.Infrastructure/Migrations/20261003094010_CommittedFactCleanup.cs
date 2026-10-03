using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CommittedFactCleanup : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "ix_outbox_confirmed_event",
                schema: "costing",
                table: "outbox",
                columns: new[] { "EventName", "DeliveredAt", "Id" },
                filter: "\"DeliveredAt\" IS NOT NULL AND \"DeadLetteredAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_outbox_confirmed_event",
                schema: "costing",
                table: "outbox");
        }
    }
}
