using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.OperationJournal.Migrations
{
    /// <inheritdoc />
    public partial class JournalCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "capacity",
                schema: "operation_journal",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RecordCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_capacity", x => x.Id);
                    table.CheckConstraint("ck_capacity_valid", "\"Id\" = 1 AND \"RecordCount\" >= 0");
                });
            migrationBuilder.Sql("""
                INSERT INTO operation_journal.capacity ("Id", "RecordCount")
                SELECT 1, count(*) FROM operation_journal.outbox;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "capacity",
                schema: "operation_journal");
        }
    }
}
