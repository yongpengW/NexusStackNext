using Microsoft.EntityFrameworkCore;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Identity.Domain.ApiResources;
using NexusStackNext.Identity.Domain.Ids;
using NexusStackNext.Identity.Domain.Menus;
using NexusStackNext.Identity.Domain.Roles;
using NexusStackNext.Identity.Domain.Tokens;
using NexusStackNext.Identity.Domain.Users;
using NexusStackNext.Identity.Domain.ValueObjects;

namespace NexusStackNext.Identity.Infrastructure.Persistence;

/// <summary>
/// Identity 上下文的持久化模型。
///
/// <para><b>它住在库的 <c>identity</c> schema 里</b>（ADR-0013：一个库、按 schema 分）。
/// 迁移历史表也跟着落在同一个 schema——这一点由基座显式配置，
/// 因为 <c>HasDefaultSchema</c> 自己**不会**把它带上。</para>
///
/// <para><b>三样参照仓库为零的东西，在这里是显式的</b>：唯一索引、CHECK 约束、并发令牌。
/// 缺了它们不会报错，只会让"重复的用户名"和"两个人同时改同一条记录"变成运行时才发现的惊喜。</para>
/// </summary>
/// <param name="options">上下文选项。</param>
public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options)
    : NexusStackDbContext(options, SchemaName)
{
    /// <summary>本上下文的 schema 名。<b>只在这里写一次</b>——配置迁移历史表时也要用它。</summary>
    public const string SchemaName = "identity";

    /// <summary>用户。</summary>
    public DbSet<User> Users => Set<User>();

    /// <summary>角色。</summary>
    public DbSet<Role> Roles => Set<Role>();

    /// <summary>API 资源。</summary>
    public DbSet<ApiResource> ApiResources => Set<ApiResource>();

    /// <summary>刷新令牌。</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    /// <summary>菜单树。</summary>
    public DbSet<MenuTree> MenuTrees => Set<MenuTree>();

    /// <inheritdoc />
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        base.ConfigureConventions(configurationBuilder);

        // 转换器在这里注册，而不是逐个属性 HasConversion：
        // 为 T 注册的约定**同时覆盖 T?**，而可空属性传非可空转换器会被泛型精确匹配拒绝。
        IdentityValueConverters.Apply(configurationBuilder);
    }

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        ConfigureUsers(modelBuilder);
        ConfigureRoles(modelBuilder);
        ConfigureApiResources(modelBuilder);
        ConfigureRefreshTokens(modelBuilder);
        ConfigureMenuTrees(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(builder =>
        {
            builder.ToTable("users");
            builder.HasKey(user => user.Id);

            builder.Property(user => user.Id)
                .ValueGeneratedNever();

            builder.Property(user => user.UserName)
                .HasMaxLength(UserName.MaxLength)
                .IsRequired();

            builder.Property(user => user.PasswordHash)
                .IsRequired();

            builder.Property(user => user.Email)
                .HasMaxLength(EmailAddress.MaxLength);

            builder.Property(user => user.Phone)
                .HasMaxLength(PhoneNumber.MaxDigits + 1);

            // **唯一索引**：原项目零唯一索引，于是"两个同名用户"只能在业务代码里靠先查后插去挡——
            // 而那有竞态。唯一性是数据库能给的、且只有数据库能给的保证。
            builder.HasIndex(user => user.UserName)
                .IsUnique()
                .HasDatabaseName("ux_users_user_name");

            // 邮箱/手机号允许为空，但**非空时唯一**。PostgreSQL 的唯一索引默认把 NULL 视为互不相同，
            // 正好就是要的语义：可以有任意多个"没填邮箱"的用户，不能有两个填了同一个邮箱的。
            builder.HasIndex(user => user.Email)
                .IsUnique()
                .HasDatabaseName("ux_users_email");

            builder.HasIndex(user => user.Phone)
                .IsUnique()
                .HasDatabaseName("ux_users_phone");

            // 角色分配存成**原始集合**（PostgreSQL 的 bigint[]）。
            //
            // 取舍说清楚：这样简单，且读"某个用户有哪些角色"是一次列读取；
            // 代价是反方向——"谁拥有这个角色"——要么全表扫、要么建 GIN 索引。
            // 当前用例只需要正方向（[IUserRepository] 只有按 ID 查），所以先按正方向存；
            // 反方向真的成为热路径时再换成关系表，那时它是一次有依据的改动。
            // 角色分配：**连接表**（`user_roles`）。
            //
            // 元素类型 `RoleId` 是一个强类型 ID（record，带 `Value`），不是实体——
            // 但 EF 允许把它当作**被拥有的类型**映射：它自己补一个影子主键，
            // 于是"一个用户有哪些角色"落成一张普通的关系表。
            //
            // 试过而不可行的两条路，记下来免得重走：
            // ① 原始集合（数组列）——EF 只接受 `List<T>` 与数组，
            //    而这个字段是 `HashSet<RoleId>`、属性暴露的是 `IReadOnlyList<RoleId>`，两种都被拒；
            // ② 换字段类型——字段是 `readonly`，EF 换不掉它。
            //
            // 这条路的额外好处：它是**关系形状**而不是数组列，
            // 所以"谁拥有这个角色"（票据 11 / 33 会问的问题）天然可查。
            builder.OwnsMany<RoleId>("_roleIds", roles =>
            {
                roles.ToTable("user_roles");
                roles.WithOwner().HasForeignKey("user_id");

                roles.Property(roleId => roleId.Value)
                    .HasColumnName("role_id")
                    .ValueGeneratedNever();

                // 键用**属性名**而不是列名：`HasKey`/`HasIndex` 认的是模型里的属性，
                // 而 `role_id` 只是 `Value` 的列名。
                //
                // **必须是复合键 `(user_id, role_id)`，不能只有 `role_id`。**
                // 只有一列的时候，"一个角色只能分配给一个用户"就成了数据库层的事实：
                // 第二个用户拿到同一个角色会主键冲突。而这是**静默**的——
                // 内存适配器不拦、领域测试不拦，只有真库上第二个用户才炸。
                // 连接表的键天然是"两端"：所有者一端 + 被拥有的一端。
                roles.HasKey("user_id", "Value");
                roles.HasIndex(roleId => roleId.Value).HasDatabaseName("ix_user_roles_role");
            });

            // **get-only 自动属性的背衬字段是 readonly，EF 的约定不映射 readonly 字段**——
            // 于是"创建后不可变"的那些字段（IsBuiltIn / IsSystem / IssuedAt…）会**静默地不进模型**，
            // 直到构造函数绑定报"参数绑不上属性"才被发现。所以它们要逐个显式声明。
            builder.Property(user => user.IsBuiltIn).IsRequired();

            builder.Property(user => user.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken();

            // **CHECK 里的列名必须与建表时的列名逐字一致。** EF 的默认命名是带引号的 PascalCase，
            // 而这些 SQL 字符串**在建模期不受任何校验**——写错了要到迁移真的跑在库上才报
            // （42703: column ... does not exist），而那时错误看起来像"表建错了"。
            builder.ToTable(table => table.HasCheckConstraint(
                "ck_users_failed_login_count",
                "\"FailedLoginCount\" >= 0"));
        });
    }

    private static void ConfigureRoles(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Role>(builder =>
        {
            builder.ToTable("roles");
            builder.HasKey(role => role.Id);

            builder.Property(role => role.Id)
                .ValueGeneratedNever();

            builder.Property(role => role.Code)
                .HasMaxLength(RoleCode.MaxLength)
                .IsRequired();

            builder.Property(role => role.Name)
                .HasMaxLength(RoleName.MaxLength)
                .IsRequired();

            // 平台是 [Flags] 枚举：存整数，将来加一个平台不会让已有数据失效。
            builder.Property(role => role.Platforms).HasConversion<int>().IsRequired();

            builder.Property(role => role.IsSystem).IsRequired();

            builder.HasIndex(role => role.Code)
                .IsUnique()
                .HasDatabaseName("ux_roles_code");
            // 菜单授权：同样是连接表（`role_menus`），理由与 `User.RoleIds` 完全相同。
            builder.OwnsMany<MenuId>("_grantedMenuIds", menus =>
            {
                menus.ToTable("role_menus");
                menus.WithOwner().HasForeignKey("role_id");

                menus.Property(menuId => menuId.Value)
                    .HasColumnName("menu_id")
                    .ValueGeneratedNever();

                // 同上：`(role_id, menu_id)` 才是连接表的键。只有 `menu_id` 时
                // "一个菜单只能授予一个角色"——第二个角色授予同一个菜单即冲突。
                menus.HasKey("role_id", "Value");
                menus.HasIndex(menuId => menuId.Value).HasDatabaseName("ix_role_menus_menu");
            });

            builder.Property(role => role.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken();
        });
    }

    private static void ConfigureApiResources(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ApiResource>(builder =>
        {
            builder.ToTable("api_resources");
            builder.HasKey(resource => resource.Id);

            builder.Property(resource => resource.Id)
                .ValueGeneratedNever();

            builder.Property(resource => resource.RoutePattern)
                .HasMaxLength(256)
                .IsRequired();

            builder.Property(resource => resource.HttpMethod)
                .HasMaxLength(16)
                .IsRequired();

            builder.Property(resource => resource.MenuId);

            // 唯一索引落在 **(路由模板, 方法)** 这一对上，而不是单独一列：
            // 同一个路由上的 GET 与 POST 是两个不同的资源。
            builder.HasIndex(resource => new { resource.RoutePattern, resource.HttpMethod })
                .IsUnique()
                .HasDatabaseName("ux_api_resources_route_method");

            builder.Property(resource => resource.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken();

            builder.ToTable(table => table.HasCheckConstraint(
                "ck_api_resources_http_method",
                "\"HttpMethod\" = upper(\"HttpMethod\")"));
        });
    }

    private static void ConfigureRefreshTokens(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.Property(token => token.SessionVersion).IsRequired();
            builder.ToTable("refresh_tokens");
            builder.HasKey(token => token.Id);

            builder.Property(token => token.Id)
                .ValueGeneratedNever();

            builder.Property(token => token.UserId)
                .IsRequired();

            builder.Property(token => token.TokenHash)
                .IsRequired();

            // 令牌哈希唯一：同一个令牌被签发两次是**不该发生**的，
            // 而它一旦发生，两行记录会让轮换逻辑做出错误的判断。
            builder.HasIndex(token => token.TokenHash)
                .IsUnique()
                .HasDatabaseName("ux_refresh_tokens_hash");

            builder.HasIndex(token => token.UserId)
                .HasDatabaseName("ix_refresh_tokens_user");

            builder.Property(token => token.IssuedAt).IsRequired();
            builder.Property(token => token.ExpiresAt).IsRequired();
            builder.Property(token => token.ConsumedAt);
            builder.Property(token => token.RevokedAt);

            builder.Property(token => token.RevokedReason).HasMaxLength(256);

            builder.Property(token => token.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken();

            builder.ToTable(table => table.HasCheckConstraint(
                "ck_refresh_tokens_expiry",
                "\"ExpiresAt\" > \"IssuedAt\""));
        });
    }

    private static void ConfigureMenuTrees(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MenuTree>(builder =>
        {
            builder.ToTable("menu_trees");
            builder.HasKey(tree => tree.Id);

            builder.Property(tree => tree.Id)
                .ValueGeneratedNever();

            builder.OwnsMany(tree => tree.Nodes, nodes =>
            {
                nodes.ToTable("menu_nodes");
                nodes.WithOwner().HasForeignKey("menu_tree_id");
                nodes.HasKey(node => node.Id);

                nodes.Property(node => node.Id)
                    .ValueGeneratedNever();

                nodes.Property(node => node.ParentId);

                nodes.Property(node => node.Path)
                    .IsRequired();

                nodes.Property(node => node.Title)
                    .HasMaxLength(MenuTitle.MaxLength)
                    .IsRequired();

                // 路径上的索引：查"谁的路径以 /1/ 开头"是这棵树最常用的问法。
                nodes.HasIndex("menu_tree_id", "Path")
                    .HasDatabaseName("ix_menu_nodes_path");
            });

            builder.Navigation(tree => tree.Nodes)
                .UsePropertyAccessMode(PropertyAccessMode.Field);

            builder.Property(tree => tree.Version)
                .ValueGeneratedNever()
                .IsConcurrencyToken();
        });
    }
}
