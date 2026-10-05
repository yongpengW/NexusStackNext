using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CapacityPolicyAuditEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "PolicyCurrentMaxPayloadBytes",
                schema: "auditing",
                table: "audit_entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyCurrentMaxRecordPayloadBytes",
                schema: "auditing",
                table: "audit_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyCurrentMaxRecords",
                schema: "auditing",
                table: "audit_entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyPreviousMaxPayloadBytes",
                schema: "auditing",
                table: "audit_entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PolicyPreviousMaxRecordPayloadBytes",
                schema: "auditing",
                table: "audit_entries",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyPreviousMaxRecords",
                schema: "auditing",
                table: "audit_entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PolicyReason",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PolicyRequestId",
                schema: "auditing",
                table: "audit_entries",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PolicyRevision",
                schema: "auditing",
                table: "audit_entries",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "auditing_fact_policy_change_valid",
                schema: "auditing",
                table: "audit_entries",
                sql: "(\"EventName\" <> \"Source\" || '.fact-capacity-policy-changed.v1' AND \"SubjectType\" <> 'fact-capacity-policy' AND \"PolicyRequestId\" IS NULL AND \"PolicyRevision\" IS NULL AND \"PolicyReason\" IS NULL\n    AND \"PolicyPreviousMaxRecords\" IS NULL AND \"PolicyPreviousMaxPayloadBytes\" IS NULL AND \"PolicyPreviousMaxRecordPayloadBytes\" IS NULL\n    AND \"PolicyCurrentMaxRecords\" IS NULL AND \"PolicyCurrentMaxPayloadBytes\" IS NULL AND \"PolicyCurrentMaxRecordPayloadBytes\" IS NULL)\nOR (\"PolicyRequestId\" IS NOT NULL AND \"PolicyRequestId\" <> '00000000-0000-0000-0000-000000000000'::uuid\n    AND \"PolicyRevision\" IS NOT NULL AND \"PolicyRevision\" > 1 AND \"PolicyRevision\" = \"SubjectVersion\"\n    AND \"PolicyReason\" IS NOT NULL AND \"PolicyReason\" = 'operator-adjustment'\n    AND \"PolicyPreviousMaxRecords\" IS NOT NULL AND \"PolicyPreviousMaxRecords\" > 0\n    AND \"PolicyPreviousMaxPayloadBytes\" IS NOT NULL AND \"PolicyPreviousMaxPayloadBytes\" > 0\n    AND \"PolicyPreviousMaxRecordPayloadBytes\" IS NOT NULL AND \"PolicyPreviousMaxRecordPayloadBytes\" > 0\n    AND \"PolicyPreviousMaxRecordPayloadBytes\" <= \"PolicyPreviousMaxPayloadBytes\"\n    AND \"PolicyCurrentMaxRecords\" IS NOT NULL AND \"PolicyCurrentMaxRecords\" > 0\n    AND \"PolicyCurrentMaxPayloadBytes\" IS NOT NULL AND \"PolicyCurrentMaxPayloadBytes\" > 0\n    AND \"PolicyCurrentMaxRecordPayloadBytes\" IS NOT NULL AND \"PolicyCurrentMaxRecordPayloadBytes\" > 0\n    AND \"PolicyCurrentMaxRecordPayloadBytes\" <= \"PolicyCurrentMaxPayloadBytes\"\n    AND (\"PolicyPreviousMaxRecords\" <> \"PolicyCurrentMaxRecords\"\n        OR \"PolicyPreviousMaxPayloadBytes\" <> \"PolicyCurrentMaxPayloadBytes\"\n        OR \"PolicyPreviousMaxRecordPayloadBytes\" <> \"PolicyCurrentMaxRecordPayloadBytes\")\n    AND \"SubjectType\" = 'fact-capacity-policy' AND \"SubjectId\" = \"Source\"\n    AND \"Action\" = \"Source\" || '.fact-capacity-policy.changed'\n    AND \"EventName\" = \"Source\" || '.fact-capacity-policy-changed.v1' AND \"ActorId\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM auditing.audit_entries WHERE "PolicyRequestId" IS NOT NULL) THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'auditing_fact_policy_history_exists',
                            MESSAGE = 'Capacity policy audit evidence prevents destructive migration rollback.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(
                name: "auditing_fact_policy_change_valid",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyCurrentMaxPayloadBytes",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyCurrentMaxRecordPayloadBytes",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyCurrentMaxRecords",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyPreviousMaxPayloadBytes",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyPreviousMaxRecordPayloadBytes",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyPreviousMaxRecords",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyReason",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyRequestId",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "PolicyRevision",
                schema: "auditing",
                table: "audit_entries");
        }
    }
}
