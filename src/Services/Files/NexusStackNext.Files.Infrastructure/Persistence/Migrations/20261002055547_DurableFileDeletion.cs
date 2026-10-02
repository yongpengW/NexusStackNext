using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Files.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DurableFileDeletion : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "BytesRemovedAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NextCleanupAttemptAt",
                schema: "files",
                table: "stored_files",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_stored_files_NextCleanupAttemptAt_Id",
                schema: "files",
                table: "stored_files",
                columns: new[] { "NextCleanupAttemptAt", "Id" },
                filter: "\"IsDeleted\" AND \"BytesRemovedAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_stored_files_NextCleanupAttemptAt_Id",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "BytesRemovedAt",
                schema: "files",
                table: "stored_files");

            migrationBuilder.DropColumn(
                name: "NextCleanupAttemptAt",
                schema: "files",
                table: "stored_files");
        }
    }
}
