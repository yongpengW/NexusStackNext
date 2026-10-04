using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>冻结的第二版容量协议：有限锁等待与不可重试的争用拒绝；后续改变须新增版本。</summary>
public static class CommittedFactCapacityMigrationV2
{
    /// <summary>替换所属触发器函数；不重新计量、重置策略或重建账本。</summary>
    /// <param name="migrationBuilder">所属迁移。</param>
    /// <param name="schema">所属 schema。</param>
    /// <param name="eventName">唯一受限事实契约。</param>
    /// <param name="triggerName">已存在的触发器函数。</param>
    public static void Install(MigrationBuilder migrationBuilder, string schema, string eventName, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        if (!IsIdentifier(schema) || !IsIdentifier(triggerName)
            || eventName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        { throw new ArgumentException("容量迁移仅接受固定的 ASCII schema、事件及触发器名称。"); }

        migrationBuilder.Sql($"""
            CREATE OR REPLACE FUNCTION "{schema}"."{triggerName}"() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE payload_size bigint;
                wait_milliseconds integer;
                previous_timeout text;
                previous_milliseconds numeric;
            BEGIN
                IF TG_OP = 'UPDATE' THEN
                    IF (OLD."EventName" = '{eventName}' OR NEW."EventName" = '{eventName}')
                        AND (OLD."Id" IS DISTINCT FROM NEW."Id" OR OLD."OccurredAt" IS DISTINCT FROM NEW."OccurredAt"
                            OR OLD."EventName" IS DISTINCT FROM NEW."EventName" OR OLD."Payload" IS DISTINCT FROM NEW."Payload") THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = '{schema}_fact_immutable', MESSAGE = 'Committed fact identity and payload cannot change';
                    END IF;
                    RETURN NULL;
                ELSIF TG_OP = 'INSERT' THEN
                    IF NEW."EventName" <> '{eventName}' THEN RETURN NULL; END IF;
                ELSIF TG_OP = 'DELETE' THEN
                    IF OLD."EventName" <> '{eventName}' THEN RETURN NULL; END IF;
                END IF;

                wait_milliseconds := coalesce(nullif(current_setting('nsn.fact_capacity_wait_ms', true), ''), '3000')::integer;
                IF wait_milliseconds < 50 OR wait_milliseconds > 30000 THEN
                    RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = '{schema}_fact_capacity_wait_invalid', MESSAGE = 'Invalid fact capacity wait budget';
                END IF;
                previous_timeout := current_setting('lock_timeout');
                previous_milliseconds := extract(epoch FROM previous_timeout::interval) * 1000;
                IF previous_milliseconds > 0 THEN
                    wait_milliseconds := least(wait_milliseconds, previous_milliseconds::integer);
                END IF;
                BEGIN
                    PERFORM set_config('lock_timeout', wait_milliseconds::text || 'ms', true);
                    IF TG_OP = 'INSERT' THEN
                        payload_size := octet_length(convert_to(NEW."Payload", 'UTF8'));
                        UPDATE "{schema}".fact_capacity
                        SET "RetainedRecords" = "RetainedRecords" + 1,
                            "RetainedPayloadBytes" = "RetainedPayloadBytes" + payload_size
                        WHERE "Id" = 1 AND "RetainedRecords" < "MaxRecords"
                            AND payload_size <= "MaxRecordPayloadBytes"
                            AND "RetainedPayloadBytes" <= "MaxPayloadBytes" - payload_size;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = '{schema}_fact_capacity_exhausted', MESSAGE = 'Committed fact capacity unavailable';
                        END IF;
                    ELSIF TG_OP = 'DELETE' THEN
                        UPDATE "{schema}".fact_capacity
                        SET "RetainedRecords" = "RetainedRecords" - 1,
                            "RetainedPayloadBytes" = "RetainedPayloadBytes" - octet_length(convert_to(OLD."Payload", 'UTF8'))
                        WHERE "Id" = 1;
                        IF NOT FOUND THEN
                            RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = '{schema}_fact_capacity_missing', MESSAGE = 'Committed fact capacity ledger missing';
                        END IF;
                    END IF;
                    PERFORM set_config('lock_timeout', previous_timeout, true);
                EXCEPTION WHEN lock_not_available THEN
                    RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = '{schema}_fact_capacity_busy', MESSAGE = 'Committed fact capacity lock wait expired';
                END;
                RETURN NULL;
            END $$;
            """);
    }

    private static bool IsIdentifier(string value) => value.Length <= 63
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
