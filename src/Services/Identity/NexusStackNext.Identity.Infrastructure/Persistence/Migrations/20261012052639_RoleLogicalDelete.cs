using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleLogicalDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                schema: "identity",
                table: "roles",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM identity.roles WHERE "IsDeleted" = TRUE) THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'identity_role_deleted_history_exists',
                            MESSAGE = 'Deleted role history prevents destructive rollback.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                schema: "identity",
                table: "roles");
        }
    }
}
