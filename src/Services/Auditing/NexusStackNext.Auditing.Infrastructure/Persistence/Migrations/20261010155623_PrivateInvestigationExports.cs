using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateInvestigationExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exports",
                schema: "auditing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalRequest = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    From = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    To = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Rows = table.Column<string>(type: "jsonb", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    ReadyAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FileId = table.Column<long>(type: "bigint", nullable: true),
                    ArtifactDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ArtifactLength = table.Column<long>(type: "bigint", nullable: true),
                    PublishedAt = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    RetryRevision = table.Column<int>(type: "integer", nullable: false),
                    ExecutionOrigin = table.Column<string>(type: "jsonb", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedBy = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_exports", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_exports_AcceptedAt_Id",
                schema: "auditing",
                table: "exports",
                columns: new[] { "AcceptedAt", "Id" },
                filter: "jsonb_array_length(\"Rows\") > 0");

            migrationBuilder.CreateIndex(
                name: "IX_exports_OwnerId_AcceptedAt_Id",
                schema: "auditing",
                table: "exports",
                columns: new[] { "OwnerId", "AcceptedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_exports_OwnerId_RequestId",
                schema: "auditing",
                table: "exports",
                columns: new[] { "OwnerId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exports_State_ReadyAt_LeaseUntil",
                schema: "auditing",
                table: "exports",
                columns: new[] { "State", "ReadyAt", "LeaseUntil" },
                filter: "\"State\" IN ('Queued', 'Generating', 'Publishing')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '5s';
                LOCK TABLE auditing.exports IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM auditing.exports) THEN
                    RAISE EXCEPTION 'auditing_exports_history_retained';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "exports",
                schema: "auditing");
        }
    }
}
