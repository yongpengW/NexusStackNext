using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>已冻结的第一版 PostgreSQL 事实容量协议；修改协议须新增版本与向前迁移。</summary>
public static class CommittedFactCapacityMigrationV1
{
    /// <summary>锁住所属 Outbox，按现有事实初始化新账本；不覆盖已存在的额度。</summary>
    /// <param name="migrationBuilder">所属迁移。</param>
    /// <param name="schema">所属 schema。</param>
    /// <param name="eventName">唯一受限的事实事件。</param>
    public static void Initialize(MigrationBuilder migrationBuilder, string schema, string eventName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        Validate(schema, eventName, "unused");
        migrationBuilder.Sql($"""
            LOCK TABLE "{schema}".outbox IN SHARE ROW EXCLUSIVE MODE;
            INSERT INTO "{schema}".fact_capacity
                ("Id", "RetainedRecords", "RetainedPayloadBytes", "MaxRecords", "MaxPayloadBytes", "MaxRecordPayloadBytes")
            SELECT 1, count(*), coalesce(sum(octet_length(convert_to("Payload", 'UTF8'))), 0), 100000, 268435456, 16384
            FROM "{schema}".outbox WHERE "EventName" = '{eventName}';
            """);
    }

    /// <summary>安装或替换原子占用、释放与不可变保护；重用既有触发器名称，保留账本和策略。</summary>
    /// <param name="migrationBuilder">所属迁移。</param>
    /// <param name="schema">所属 schema。</param>
    /// <param name="eventName">唯一受限的事实事件。</param>
    /// <param name="triggerName">本上下文的触发器及函数名。</param>
    public static void Install(MigrationBuilder migrationBuilder, string schema, string eventName, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        Validate(schema, eventName, triggerName);
        migrationBuilder.Sql($"""
            CREATE OR REPLACE FUNCTION "{schema}"."{triggerName}"() RETURNS trigger LANGUAGE plpgsql AS $$
            DECLARE payload_size bigint;
            BEGIN
                IF TG_OP = 'UPDATE' THEN
                    IF (OLD."EventName" = '{eventName}' OR NEW."EventName" = '{eventName}')
                        AND (OLD."Id" IS DISTINCT FROM NEW."Id" OR OLD."OccurredAt" IS DISTINCT FROM NEW."OccurredAt"
                            OR OLD."EventName" IS DISTINCT FROM NEW."EventName" OR OLD."Payload" IS DISTINCT FROM NEW."Payload") THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = '{schema}_fact_immutable', MESSAGE = 'Committed fact identity and payload cannot change';
                    END IF;
                    RETURN NULL;
                ELSIF TG_OP = 'INSERT' AND NEW."EventName" = '{eventName}' THEN
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
                ELSIF TG_OP = 'DELETE' AND OLD."EventName" = '{eventName}' THEN
                    UPDATE "{schema}".fact_capacity
                    SET "RetainedRecords" = "RetainedRecords" - 1,
                        "RetainedPayloadBytes" = "RetainedPayloadBytes" - octet_length(convert_to(OLD."Payload", 'UTF8'))
                    WHERE "Id" = 1;
                    IF NOT FOUND THEN
                        RAISE EXCEPTION USING ERRCODE = '23514', CONSTRAINT = '{schema}_fact_capacity_missing', MESSAGE = 'Committed fact capacity ledger missing';
                    END IF;
                END IF;
                RETURN NULL;
            END $$;
            DROP TRIGGER IF EXISTS "{triggerName}" ON "{schema}".outbox;
            CREATE TRIGGER "{triggerName}" AFTER INSERT OR UPDATE OR DELETE ON "{schema}".outbox
                FOR EACH ROW EXECUTE FUNCTION "{schema}"."{triggerName}"();
            """);
    }

    /// <summary>撤销本版触发器；由所属迁移决定账本表的撤销。</summary>
    /// <param name="migrationBuilder">所属迁移。</param>
    /// <param name="schema">所属 schema。</param>
    /// <param name="triggerName">本上下文的触发器及函数名。</param>
    public static void Remove(MigrationBuilder migrationBuilder, string schema, string triggerName)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        Validate(schema, "unused", triggerName);
        migrationBuilder.Sql($"""
            DROP TRIGGER "{triggerName}" ON "{schema}".outbox;
            DROP FUNCTION "{schema}"."{triggerName}"();
            """);
    }

    private static void Validate(string schema, string eventName, string triggerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        if (!IsIdentifier(schema) || !IsIdentifier(triggerName)
            || eventName.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '-' and not '_'))
        {
            throw new ArgumentException("容量迁移仅接受固定的 ASCII schema、事件及触发器名称。");
        }
    }

    private static bool IsIdentifier(string value) => value.Length <= 63
        && (char.IsAsciiLetter(value[0]) || value[0] == '_')
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
}
