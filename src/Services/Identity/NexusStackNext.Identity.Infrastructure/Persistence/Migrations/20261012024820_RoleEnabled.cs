using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class RoleEnabled : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsEnabled", schema: "identity", table: "roles",
                type: "boolean", nullable: false, defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM identity.roles WHERE "IsEnabled" = FALSE) THEN
                        RAISE EXCEPTION USING ERRCODE = 'P0001', CONSTRAINT = 'identity_role_enabled_history_exists',
                            MESSAGE = 'Disabled role state prevents destructive rollback.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropColumn(name: "IsEnabled", schema: "identity", table: "roles");
        }
    }
}
