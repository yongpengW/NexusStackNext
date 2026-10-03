using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Auditing.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommittedFactRelatedSubject : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RelatedContext",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelatedSubjectId",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RelatedSubjectType",
                schema: "auditing",
                table: "audit_entries",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_audit_entries_related",
                schema: "auditing",
                table: "audit_entries",
                columns: new[] { "RelatedContext", "RelatedSubjectType", "RelatedSubjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_audit_entries_related",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "RelatedContext",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "RelatedSubjectId",
                schema: "auditing",
                table: "audit_entries");

            migrationBuilder.DropColumn(
                name: "RelatedSubjectType",
                schema: "auditing",
                table: "audit_entries");
        }
    }
}
