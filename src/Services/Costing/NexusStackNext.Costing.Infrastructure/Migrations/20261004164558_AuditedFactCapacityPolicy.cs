using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Costing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AuditedFactCapacityPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact_policy_control",
                schema: "costing",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    PolicyRevision = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecords = table.Column<long>(type: "bigint", nullable: false),
                    MaxPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecordPayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    RetainedRecords = table.Column<long>(type: "bigint", nullable: false),
                    RetainedPayloadBytes = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_policy_control", x => x.Id);
                    table.CheckConstraint("ck_fact_policy_control_bounds", "\"PolicyRevision\" > 0 AND \"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\" AND \"RetainedRecords\" >= 0 AND \"RetainedRecords\" <= \"MaxRecords\" AND \"RetainedPayloadBytes\" >= 0 AND \"RetainedPayloadBytes\" <= \"MaxPayloadBytes\"");
                    table.CheckConstraint("ck_fact_policy_control_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "fact_policy_receipts",
                schema: "costing",
                columns: table => new
                {
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RecordJson = table.Column<string>(type: "text", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_policy_receipts", x => x.RequestId);
                    table.CheckConstraint("ck_fact_policy_receipt_bytes", "\"PayloadBytes\" > 0");
                    table.ForeignKey(
                        name: "FK_fact_policy_receipts_outbox_EventId",
                        column: x => x.EventId,
                        principalSchema: "costing",
                        principalTable: "outbox",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                schema: "costing",
                table: "fact_policy_control",
                columns: new[] { "Id", "MaxPayloadBytes", "MaxRecordPayloadBytes", "MaxRecords", "PolicyRevision", "RetainedPayloadBytes", "RetainedRecords" },
                values: new object[] { 1, 16777216L, 16384, 1000L, 1L, 0L, 0L });

            migrationBuilder.CreateIndex(
                name: "IX_fact_policy_receipts_EventId",
                schema: "costing",
                table: "fact_policy_receipts",
                column: "EventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_fact_policy_receipts_RetainUntil_RequestId",
                schema: "costing",
                table: "fact_policy_receipts",
                columns: new[] { "RetainUntil", "RequestId" });
            migrationBuilder.Sql("""
                CREATE FUNCTION costing.guard_fact_policy_evidence() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF (OLD."EventName" = 'costing.fact-capacity-policy-changed.v1'
                            OR NEW."EventName" = 'costing.fact-capacity-policy-changed.v1')
                        AND (OLD."Id" IS DISTINCT FROM NEW."Id" OR OLD."OccurredAt" IS DISTINCT FROM NEW."OccurredAt"
                            OR OLD."EventName" IS DISTINCT FROM NEW."EventName" OR OLD."Payload" IS DISTINCT FROM NEW."Payload") THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'costing_fact_policy_immutable',
                            MESSAGE = 'Accepted policy fact identity and payload cannot change.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_fact_policy_evidence BEFORE UPDATE ON costing.outbox
                    FOR EACH ROW EXECUTE FUNCTION costing.guard_fact_policy_evidence();
                CREATE FUNCTION costing.guard_fact_policy_receipt() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    IF OLD IS DISTINCT FROM NEW THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'costing_fact_policy_receipt_immutable',
                            MESSAGE = 'Accepted policy receipt cannot change.';
                    END IF;
                    RETURN NEW;
                END $$;
                CREATE TRIGGER guard_fact_policy_receipt BEFORE UPDATE ON costing.fact_policy_receipts
                    FOR EACH ROW EXECUTE FUNCTION costing.guard_fact_policy_receipt();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM costing.fact_policy_receipts)
                        OR EXISTS (SELECT 1 FROM costing.fact_policy_control WHERE "PolicyRevision" <> 1) THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'costing_fact_policy_history_exists',
                            MESSAGE = 'Accepted fact policy history prevents destructive rollback.';
                    END IF;
                END $$;
                DROP TRIGGER guard_fact_policy_evidence ON costing.outbox;
                DROP FUNCTION costing.guard_fact_policy_evidence();
                DROP TRIGGER guard_fact_policy_receipt ON costing.fact_policy_receipts;
                DROP FUNCTION costing.guard_fact_policy_receipt();
                """);
            migrationBuilder.DropTable(
                name: "fact_policy_control",
                schema: "costing");

            migrationBuilder.DropTable(
                name: "fact_policy_receipts",
                schema: "costing");
        }
    }
}
