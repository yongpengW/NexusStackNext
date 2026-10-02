using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OperationObservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "operation_observations",
                schema: "auditing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    HttpMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    RouteTemplate = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    DurationMs = table.Column<long>(type: "bigint", nullable: true),
                    RecordedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_observations", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_operation_observations_time",
                schema: "auditing",
                table: "operation_observations",
                columns: new[] { "OccurredAt", "Source", "OperationId" });

            migrationBuilder.CreateIndex(
                name: "ux_operation_observations_phase",
                schema: "auditing",
                table: "operation_observations",
                columns: new[] { "Source", "OperationId", "Phase" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_observations",
                schema: "auditing");
        }
    }
}
