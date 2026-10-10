using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations;

/// <summary>观察保留扫描使用接收时间索引，保留既有记录与计量。</summary>
[DbContext(typeof(AuditingDbContext))]
[Migration("20261010022000_ObservationRetentionIndex")]
public sealed class ObservationRetentionIndex : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.CreateIndex(
        name: "ix_operation_observations_recorded", schema: "auditing", table: "operation_observations", columns: ["RecordedAt", "Id"]);

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropIndex(
        name: "ix_operation_observations_recorded", schema: "auditing", table: "operation_observations");
}
