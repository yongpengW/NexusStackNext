using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace NexusStackNext.Identity.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "identity");

            migrationBuilder.CreateTable(
                name: "api_resources",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    RoutePattern = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    HttpMethod = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    MenuId = table.Column<long>(type: "bigint", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_api_resources", x => x.Id);
                    table.CheckConstraint("ck_api_resources_http_method", "\"HttpMethod\" = upper(\"HttpMethod\")");
                });

            migrationBuilder.CreateTable(
                name: "inbox",
                schema: "identity",
                columns: table => new
                {
                    ConsumerName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    MessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_inbox", x => new { x.ConsumerName, x.EventName, x.MessageId });
                });

            migrationBuilder.CreateTable(
                name: "menu_trees",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_menu_trees", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFailure = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    UserId = table.Column<long>(type: "bigint", nullable: false),
                    TokenHash = table.Column<string>(type: "text", nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.CheckConstraint("ck_refresh_tokens_expiry", "\"ExpiresAt\" > \"IssuedAt\"");
                });

            migrationBuilder.CreateTable(
                name: "roles",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Platforms = table.Column<int>(type: "integer", nullable: false),
                    IsSystem = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_roles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    UserName = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Phone = table.Column<string>(type: "character varying(21)", maxLength: 21, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsBuiltIn = table.Column<bool>(type: "boolean", nullable: false),
                    FailedLoginCount = table.Column<int>(type: "integer", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                    table.CheckConstraint("ck_users_failed_login_count", "\"FailedLoginCount\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "menu_nodes",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false),
                    ParentId = table.Column<long>(type: "bigint", nullable: true),
                    Path = table.Column<string>(type: "text", nullable: false),
                    Title = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    menu_tree_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_menu_nodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_menu_nodes_menu_trees_menu_tree_id",
                        column: x => x.menu_tree_id,
                        principalSchema: "identity",
                        principalTable: "menu_trees",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "role_menus",
                schema: "identity",
                columns: table => new
                {
                    menu_id = table.Column<long>(type: "bigint", nullable: false),
                    role_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_menus", x => x.menu_id);
                    table.ForeignKey(
                        name: "FK_role_menus_roles_role_id",
                        column: x => x.role_id,
                        principalSchema: "identity",
                        principalTable: "roles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_roles",
                schema: "identity",
                columns: table => new
                {
                    role_id = table.Column<long>(type: "bigint", nullable: false),
                    user_id = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_roles", x => x.role_id);
                    table.ForeignKey(
                        name: "FK_user_roles_users_user_id",
                        column: x => x.user_id,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ux_api_resources_route_method",
                schema: "identity",
                table: "api_resources",
                columns: new[] { "RoutePattern", "HttpMethod" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_menu_nodes_path",
                schema: "identity",
                table: "menu_nodes",
                columns: new[] { "menu_tree_id", "Path" });

            migrationBuilder.CreateIndex(
                name: "ix_outbox_pending",
                schema: "identity",
                table: "outbox",
                columns: new[] { "DeliveredAt", "DeadLetteredAt", "NextAttemptAt" });

            migrationBuilder.CreateIndex(
                name: "ix_refresh_tokens_user",
                schema: "identity",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "ux_refresh_tokens_hash",
                schema: "identity",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_role_menus_menu",
                schema: "identity",
                table: "role_menus",
                column: "menu_id");

            migrationBuilder.CreateIndex(
                name: "IX_role_menus_role_id",
                schema: "identity",
                table: "role_menus",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "ux_roles_code",
                schema: "identity",
                table: "roles",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_user_roles_role",
                schema: "identity",
                table: "user_roles",
                column: "role_id");

            migrationBuilder.CreateIndex(
                name: "IX_user_roles_user_id",
                schema: "identity",
                table: "user_roles",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "ux_users_email",
                schema: "identity",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_phone",
                schema: "identity",
                table: "users",
                column: "Phone",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_users_user_name",
                schema: "identity",
                table: "users",
                column: "UserName",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "api_resources",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "inbox",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "menu_nodes",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "outbox",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "refresh_tokens",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "role_menus",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "user_roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "menu_trees",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "roles",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "users",
                schema: "identity");
        }
    }
}
