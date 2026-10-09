using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Files.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PrivateGeneratedFileProtocol : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "Candidate_ColumnSetVersion",
                schema: "files",
                table: "stored_files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Candidate_ExpiredAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Candidate_ExpiresAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Candidate_Format",
                schema: "files",
                table: "stored_files",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Candidate_FormatVersion",
                schema: "files",
                table: "stored_files",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "Candidate_Length",
                schema: "files",
                table: "stored_files",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Candidate_OwnerId",
                schema: "files",
                table: "stored_files",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Candidate_Producer",
                schema: "files",
                table: "stored_files",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Candidate_PublicationId",
                schema: "files",
                table: "stored_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Candidate_PublishedAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Candidate_SealedAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Candidate_Sha256",
                schema: "files",
                table: "stored_files",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Candidate_SourceExportId",
                schema: "files",
                table: "stored_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "Candidate_StageExpiresAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "Candidate_UploadId",
                schema: "files",
                table: "stored_files",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "CandidateDeadline",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true,
                computedColumnSql: "COALESCE(\"Candidate_ExpiresAt\", \"Candidate_StageExpiresAt\")",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_stored_files_Candidate_Producer_Candidate_PublicationId",
                schema: "files",
                table: "stored_files",
                columns: new[] { "Candidate_Producer", "Candidate_PublicationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stored_files_Candidate_Producer_Candidate_UploadId",
                schema: "files",
                table: "stored_files",
                columns: new[] { "Candidate_Producer", "Candidate_UploadId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_stored_files_CandidateDeadline_Id",
                schema: "files",
                table: "stored_files",
                columns: new[] { "CandidateDeadline", "Id" },
                filter: "NOT \"IsDeleted\" AND \"Candidate_Producer\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stored_files_Candidate_Producer_Candidate_PublicationId",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropIndex(
                name: "IX_stored_files_Candidate_Producer_Candidate_UploadId",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropIndex(
                name: "IX_stored_files_CandidateDeadline_Id",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "CandidateDeadline",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_ColumnSetVersion",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_ExpiredAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_ExpiresAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_Format",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_FormatVersion",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_Length",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_OwnerId",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_Producer",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_PublicationId",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_PublishedAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_SealedAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_Sha256",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_SourceExportId",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_StageExpiresAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "Candidate_UploadId",
                schema: "files",
                table: "stored_files");
        }
    }
}
