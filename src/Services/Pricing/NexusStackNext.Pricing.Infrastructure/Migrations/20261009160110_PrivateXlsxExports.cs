using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Pricing.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PrivateXlsxExports : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ArtifactDigest",
                schema: "pricing",
                table: "exports",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ArtifactLength",
                schema: "pricing",
                table: "exports",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Format",
                schema: "pricing",
                table: "exports",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "csv");

            // 已选定的 CSV 字节就是原快照文本；回填后原发布意图可以继续恢复。
            migrationBuilder.Sql("""
                UPDATE pricing.exports
                SET "ArtifactDigest" = "SnapshotDigest", "ArtifactLength" = "SnapshotLength"
                WHERE "FileId" IS NOT NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 防止分步降级先抹掉格式/字节身份，之后才被旧迁移的历史保护拒绝。
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
            migrationBuilder.DropColumn(
                name: "ArtifactDigest",
                schema: "pricing",
                table: "exports");

            migrationBuilder.DropColumn(
                name: "ArtifactLength",
                schema: "pricing",
                table: "exports");

            migrationBuilder.DropColumn(
                name: "Format",
                schema: "pricing",
                table: "exports");
        }
    }
}
