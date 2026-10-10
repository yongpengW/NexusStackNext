using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations;

/// <summary>中央记录计量第一版：迁移时回填，之后在记录事务内原子计量。</summary>
[DbContext(typeof(AuditingDbContext))]
[Migration("20261010010500_CentralAuditCapacity")]
public sealed class CentralAuditCapacity : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            SET LOCAL lock_timeout = '3s';
            LOCK TABLE auditing.audit_entries, auditing.operation_observations IN SHARE ROW EXCLUSIVE MODE;
            CREATE TABLE auditing.central_storage_capacity (
                "Pool" text PRIMARY KEY CHECK ("Pool" IN ('facts', 'observations')),
                "Records" bigint NOT NULL CHECK ("Records" >= 0)
            );
            INSERT INTO auditing.central_storage_capacity ("Pool", "Records")
            VALUES ('facts', (SELECT count(*) FROM auditing.audit_entries)),
                   ('observations', (SELECT count(*) FROM auditing.operation_observations));

            CREATE FUNCTION auditing.central_storage_capacity_v1() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE pool_name text := TG_ARGV[0];
                max_records bigint;
            BEGIN
                IF TG_OP = 'INSERT' THEN
                    max_records := coalesce(nullif(current_setting('nsn.auditing_max_' || pool_name, true), ''),
                        CASE WHEN pool_name = 'facts' THEN '1000000' ELSE '2000000' END)::bigint;
                    IF max_records < 1 OR max_records > 1000000000 THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'auditing_capacity_invalid', MESSAGE = 'Invalid central audit limit';
                    END IF;
                    UPDATE auditing.central_storage_capacity SET "Records" = "Records" + 1
                        WHERE "Pool" = pool_name AND "Records" < max_records;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'auditing_capacity_exhausted', MESSAGE = 'Central audit capacity unavailable';
                    END IF;
                ELSIF TG_OP = 'DELETE' THEN
                    UPDATE auditing.central_storage_capacity SET "Records" = "Records" - 1 WHERE "Pool" = pool_name;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'auditing_capacity_missing', MESSAGE = 'Central audit capacity ledger missing';
                    END IF;
                ELSE
                    UPDATE auditing.central_storage_capacity SET "Records" = 0 WHERE "Pool" = pool_name;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = 'auditing_capacity_missing', MESSAGE = 'Central audit capacity ledger missing';
                    END IF;
                END IF;
                RETURN NULL;
            END $$;
            CREATE TRIGGER audit_entries_capacity AFTER INSERT OR DELETE ON auditing.audit_entries
                FOR EACH ROW EXECUTE FUNCTION auditing.central_storage_capacity_v1('facts');
            CREATE TRIGGER operation_observations_capacity AFTER INSERT OR DELETE ON auditing.operation_observations
                FOR EACH ROW EXECUTE FUNCTION auditing.central_storage_capacity_v1('observations');
            CREATE TRIGGER audit_entries_capacity_truncate AFTER TRUNCATE ON auditing.audit_entries
                FOR EACH STATEMENT EXECUTE FUNCTION auditing.central_storage_capacity_v1('facts');
            CREATE TRIGGER operation_observations_capacity_truncate AFTER TRUNCATE ON auditing.operation_observations
                FOR EACH STATEMENT EXECUTE FUNCTION auditing.central_storage_capacity_v1('observations');
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TRIGGER audit_entries_capacity ON auditing.audit_entries;
            DROP TRIGGER audit_entries_capacity_truncate ON auditing.audit_entries;
            DROP TRIGGER operation_observations_capacity ON auditing.operation_observations;
            DROP TRIGGER operation_observations_capacity_truncate ON auditing.operation_observations;
            DROP FUNCTION auditing.central_storage_capacity_v1();
            DROP TABLE auditing.central_storage_capacity;
            """);
    }
}
