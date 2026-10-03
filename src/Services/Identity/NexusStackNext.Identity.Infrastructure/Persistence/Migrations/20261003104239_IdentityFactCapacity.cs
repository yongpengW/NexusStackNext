using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityFactCapacity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "fact_capacity",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    RetainedRecords = table.Column<long>(type: "bigint", nullable: false),
                    RetainedPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecords = table.Column<long>(type: "bigint", nullable: false),
                    MaxPayloadBytes = table.Column<long>(type: "bigint", nullable: false),
                    MaxRecordPayloadBytes = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fact_capacity", x => x.Id);
                    table.CheckConstraint("ck_fact_capacity_bounds", "\"RetainedRecords\" >= 0 AND \"RetainedPayloadBytes\" >= 0 AND \"MaxRecords\" > 0 AND \"MaxPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" > 0 AND \"MaxRecordPayloadBytes\" <= \"MaxPayloadBytes\"");
                    table.CheckConstraint("ck_fact_capacity_singleton", "\"Id\" = 1");
                });
            migrationBuilder.Sql("""
                LOCK TABLE identity.outbox IN SHARE ROW EXCLUSIVE MODE;
                INSERT INTO identity.fact_capacity
                    ("Id", "RetainedRecords", "RetainedPayloadBytes", "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes")
                SELECT 1, count(*), coalesce(sum(octet_length(convert_to("Payload", 'UTF8'))), 0), 100000, 268435456, 16384
                FROM identity.outbox WHERE "EventName" = 'identity.entity-committed.v1';

                CREATE FUNCTION identity.account_identity_fact() RETURNS trigger LANGUAGE plpgsql AS $$
                DECLARE payload_size bigint;
                BEGIN
                    IF TG_OP = 'UPDATE' THEN
                        IF (OLD."EventName" = 'identity.entity-committed.v1' OR NEW."EventName" = 'identity.entity-committed.v1')
                            AND (OLD."Id" IS DISTINCT FROM NEW."Id" OR OLD."OccurredAt" IS DISTINCT FROM NEW."OccurredAt"
                                OR OLD."EventName" IS DISTINCT FROM NEW."EventName" OR OLD."Payload" IS DISTINCT FROM NEW."Payload") THEN
                            RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'identity_fact_immutable', MESSAGE = 'Committed fact identity and payload cannot change';
                        END IF;
                        RETURN NULL;
                    ELSIF TG_OP = 'INSERT' AND NEW."EventName" = 'identity.entity-committed.v1' THEN
                        payload_size := octet_length(convert_to(NEW."Payload", 'UTF8'));
                        UPDATE identity.fact_capacity
                        SET "RetainedRecords" = "RetainedRecords" + 1,
                            "RetainedPayloadBytes" = "RetainedPayloadBytes" + payload_size
                        WHERE "Id" = 1 AND "RetainedRecords" < "MaxRecords"
                            AND payload_size <= "MaxRecordPayloadBytes"
                            AND "RetainedPayloadBytes" <= "MaxPayloadBytes" - payload_size;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'identity_fact_capacity_exhausted', MESSAGE = 'Committed fact capacity unavailable';
                        END IF;
                    ELSIF TG_OP = 'DELETE' AND OLD."EventName" = 'identity.entity-committed.v1' THEN
                        UPDATE identity.fact_capacity
                        SET "RetainedRecords" = "RetainedRecords" - 1,
                            "RetainedPayloadBytes" = "RetainedPayloadBytes" - octet_length(convert_to(OLD."Payload", 'UTF8'))
                        WHERE "Id" = 1;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'identity_fact_capacity_missing', MESSAGE = 'Committed fact capacity ledger missing';
                        END IF;
                    END IF;
                    RETURN NULL;
                END $$;
                CREATE TRIGGER account_identity_fact AFTER INSERT OR UPDATE OR DELETE ON identity.outbox
                    FOR EACH ROW EXECUTE FUNCTION identity.account_identity_fact();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP TRIGGER account_identity_fact ON identity.outbox;
                DROP FUNCTION identity.account_identity_fact();
                """);
            migrationBuilder.DropTable(
                name: "fact_capacity",
                schema: "identity");
        }
    }
}
