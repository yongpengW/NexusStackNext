using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Scheduling.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CalendarRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<TimeSpan>(
                name: "Interval",
                schema: "scheduling",
                table: "plans",
                type: "interval",
                nullable: true,
                oldClrType: typeof(TimeSpan),
                oldType: "interval");

            migrationBuilder.AddColumn<int>(
                name: "CronFieldCount",
                schema: "scheduling",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Day",
                schema: "scheduling",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Expression",
                schema: "scheduling",
                table: "plans",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "GraceSeconds",
                schema: "scheduling",
                table: "plans",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Hour",
                schema: "scheduling",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Minute",
                schema: "scheduling",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MisfirePolicy",
                schema: "scheduling",
                table: "plans",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RuleKind",
                schema: "scheduling",
                table: "plans",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Interval");

            migrationBuilder.AddColumn<long>(
                name: "ScheduleRevision",
                schema: "scheduling",
                table: "plans",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                schema: "scheduling",
                table: "plans",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 日历计划没有 Interval，不能静默降级成零秒计划。
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM scheduling.plans WHERE "RuleKind" <> 'Interval') THEN
                        RAISE EXCEPTION 'Calendar plans must be explicitly removed or converted before this downgrade.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "CronFieldCount",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Day",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Expression",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "GraceSeconds",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Hour",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "Minute",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "MisfirePolicy",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "RuleKind",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "ScheduleRevision",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                schema: "scheduling",
                table: "plans");

            migrationBuilder.AlterColumn<TimeSpan>(
                name: "Interval",
                schema: "scheduling",
                table: "plans",
                type: "interval",
                nullable: false,
                defaultValue: new TimeSpan(0, 0, 0, 0, 0),
                oldClrType: typeof(TimeSpan),
                oldType: "interval",
                oldNullable: true);
        }
    }
}
