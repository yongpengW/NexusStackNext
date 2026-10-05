using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ConditionalFactRecovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact_recovery_control",
                schema: "pricing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    MaxRecords = table.Column<long>(type: "bigint", nullable: false),
                    MaxPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecordPayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    RetainedRecords = table.Column<long>(type: "bigint", nullable: false),
                    RetainedPayloadBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_recovery_control", x => x.Id);
                    table.CheckConstraint("ck_fact_recovery_control_bounds", "\"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\" AND \"RetainedRecords\" >= 0 AND \"RetainedRecords\" <= \"MaxRecords\" AND \"RetainedPayloadBytes\" >= 0 AND \"RetainedPayloadBytes\" <= \"MaxPayloadBytes\"");
                    table.CheckConstraint("ck_fact_recovery_control_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "fact_recovery_receipts",
                schema: "pricing",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordJson = table.Column<string>(type: "text", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_recovery_receipts", x => x.RequestId);
                    table.CheckConstraint("ck_fact_recovery_receipt_bytes", "\"PayloadBytes\" > 0");
                });

            migrationBuilder.InsertData(
                schema: "pricing",
                table: "fact_recovery_control",
                columns: new[] { "Id", "MaxPayloadBytes", "MaxRecordPayloadBytes", "MaxRecords", "RetainedPayloadBytes", "RetainedRecords" },
                values: new object[] { 1, 16777216L, 16384, 1000L, 0L, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_fact_recovery_receipts_RetainUntil_RequestId",
                schema: "pricing",
                table: "fact_recovery_receipts",
                columns: new[] { "RetainUntil", "RequestId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION pricing.guard_fact_recovery_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD IS DISTINCT FROM NEW THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'pricing_fact_recovery_receipt_immutable',
                            MESSAGE = 'Accepted fact recovery receipt cannot change.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_fact_recovery_receipt BEFORE UPDATE ON pricing.fact_recovery_receipts
                    FOR EACH ROW EXECUTE FUNCTION pricing.guard_fact_recovery_receipt();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                LOCK TABLE pricing.fact_recovery_control, pricing.fact_recovery_receipts IN ACCESS EXCLUSIVE MODE;
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pricing.fact_recovery_receipts)
                        OR EXISTS (SELECT 1 FROM pricing.fact_recovery_control
                            WHERE "RetainedRecords" <> 0 OR "RetainedPayloadBytes" <> 0) THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'pricing_fact_recovery_history_exists',
                            MESSAGE = 'Accepted fact recovery history prevents destructive rollback.';
                    END IF;
                END $$;
                DROP TRIGGER guard_fact_recovery_receipt ON pricing.fact_recovery_receipts;
                DROP FUNCTION pricing.guard_fact_recovery_receipt();
                """);
            migrationBuilder.DropTable(
                name: "fact_recovery_control",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "fact_recovery_receipts",
                schema: "pricing");
        }
    }
}
