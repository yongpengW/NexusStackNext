using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PrivatePricingExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "exports",
                schema: "pricing",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    CanonicalRequest = table.Column<string>(type: "character varying(262144)", maxLength: 262144, nullable: false),
                    RequestDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestDigestVersion = table.Column<int>(type: "integer", nullable: false),
                    SnapshotDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotLength = table.Column<long>(type: "bigint", nullable: false),
                    SnapshotDigestVersion = table.Column<int>(type: "integer", nullable: false),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Rows = table.Column<string>(type: "jsonb", nullable: false),
                    RowCount = table.Column<int>(type: "integer", nullable: false, computedColumnSql: "jsonb_array_length(\"Rows\")", stored: true),
                    State = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RetryRevision = table.Column<long>(type: "bigint", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FileId = table.Column<long>(type: "bigint", nullable: true),
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Producer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    PublicationSelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedAt = table.Column<long>(type: "bigint", nullable: true),
                    ExpiresAt = table.Column<long>(type: "bigint", nullable: true),
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

            migrationBuilder.CreateTable(
                name: "export_publications",
                schema: "pricing",
                columns: table => new
                {
                    PublicationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExportId = table.Column<Guid>(type: "uuid", nullable: false),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileId = table.Column<long>(type: "bigint", nullable: false),
                    Producer = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SelectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Epoch = table.Column<long>(type: "bigint", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    RetryRevision = table.Column<long>(type: "bigint", nullable: false),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MaxLeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StoppedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_export_publications", x => x.PublicationId);
                    table.ForeignKey(
                        name: "FK_export_publications_exports_ExportId",
                        column: x => x.ExportId,
                        principalSchema: "pricing",
                        principalTable: "exports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_export_publications_ExportId",
                schema: "pricing",
                table: "export_publications",
                column: "ExportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_export_publications_State_AvailableAt_PublicationId",
                schema: "pricing",
                table: "export_publications",
                columns: new[] { "State", "AvailableAt", "PublicationId" });

            migrationBuilder.CreateIndex(
                name: "IX_exports_AvailableAt_AcceptedAt_Id",
                schema: "pricing",
                table: "exports",
                columns: new[] { "AvailableAt", "AcceptedAt", "Id" },
                filter: "\"State\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_exports_LeaseUntil_AcceptedAt_Id",
                schema: "pricing",
                table: "exports",
                columns: new[] { "LeaseUntil", "AcceptedAt", "Id" },
                filter: "\"State\" = 'Generating'");

            migrationBuilder.CreateIndex(
                name: "IX_exports_OwnerId_AcceptedAt_Id",
                schema: "pricing",
                table: "exports",
                columns: new[] { "OwnerId", "AcceptedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_exports_OwnerId_RequestId",
                schema: "pricing",
                table: "exports",
                columns: new[] { "OwnerId", "RequestId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_exports_PublicationId",
                schema: "pricing",
                table: "exports",
                column: "PublicationId",
                unique: true,
                filter: "\"PublicationId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 先在同一迁移事务阻止新的接受/发布写入，再判断历史；只看 EXISTS 会漏掉正在 COMMIT 的接受者。
            migrationBuilder.Sql("""
                SET LOCAL lock_timeout = '2s';
                SET LOCAL statement_timeout = '5s';
                LOCK TABLE pricing.exports, pricing.export_publications IN ACCESS EXCLUSIVE MODE;
                DO $$ BEGIN
                  IF EXISTS (SELECT 1 FROM pricing.exports) OR EXISTS (SELECT 1 FROM pricing.export_publications) THEN
                    RAISE EXCEPTION 'pricing_exports_history_retained';
                  END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "export_publications",
                schema: "pricing");

            migrationBuilder.DropTable(
                name: "exports",
                schema: "pricing");
        }
    }
}
