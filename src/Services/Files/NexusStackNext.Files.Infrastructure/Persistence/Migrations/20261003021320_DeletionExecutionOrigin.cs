using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Files.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class DeletionExecutionOrigin : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeletionOrigin",
                schema: "files",
                table: "stored_files",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletionOrigin",
                schema: "files",
                table: "stored_files");
        }
    }
}
