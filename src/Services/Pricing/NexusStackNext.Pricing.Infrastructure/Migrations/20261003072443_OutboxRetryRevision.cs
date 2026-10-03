using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OutboxRetryRevision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RetryRevision",
                schema: "pricing",
                table: "outbox",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetryRevision",
                schema: "pricing",
                table: "outbox");
        }
    }
}
