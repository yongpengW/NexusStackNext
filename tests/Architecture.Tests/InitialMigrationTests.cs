using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace NexusStackNext.Architecture.Tests;

/// <summary>检查真实迁移的公开操作，防止实体有审计契约而建表脚本仍然漏列。</summary>
public sealed class InitialMigrationTests
{
    private static readonly Dictionary<string, string[]> AuditedTables = new(StringComparer.Ordinal)
    {
        ["Identity"] = ["api_resources", "menu_trees", "roles", "users"],
        ["Platform"] = ["global_settings"],
        ["Files"] = ["stored_files"],
        ["Auditing"] = [],
        ["Scheduling"] = ["plans"],
        ["Costing"] = ["sheets"],
        ["Pricing"] = ["quotes"],
    };

    [Fact]
    public void DevelopmentContexts_StartWithOneCompleteInitialMigration()
    {
        var migrations = InitialMigrations();
        Assert.Equal(7, migrations.Count);
        foreach (var (context, migration) in migrations)
        {
            var tables = migration.UpOperations.OfType<CreateTableOperation>().ToArray();
            Assert.NotEmpty(tables);
            Assert.All(tables, table => Assert.Equal(context.ToLowerInvariant(), table.Schema));
            Assert.Contains(tables, table => table.Name == "inbox");
            Assert.Contains(tables, table => table.Name == "outbox");
            Assert.DoesNotContain(migration.UpOperations, operation => operation is AlterColumnOperation or AddColumnOperation);
        }
    }

    [Fact]
    public void BusinessTables_CreateAuditColumnsWithoutFabricatedDefaults()
    {
        var checkedTables = 0;
        foreach (var (context, migration) in InitialMigrations())
        {
            foreach (var name in AuditedTables[context])
            {
                var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>(), table => table.Name == name);
                var createdAt = Column(table, "CreatedAt");
                Assert.Equal(typeof(DateTimeOffset), createdAt.ClrType);
                Assert.False(createdAt.IsNullable);
                Assert.Null(createdAt.DefaultValue);
                Assert.Null(createdAt.DefaultValueSql);
                var updatedAt = Column(table, "UpdatedAt");
                Assert.Equal(typeof(DateTimeOffset), updatedAt.ClrType);
                Assert.True(updatedAt.IsNullable);
                foreach (var actor in new[] { "CreatedBy", "UpdatedBy" })
                {
                    var column = Column(table, actor);
                    Assert.Equal(typeof(string), column.ClrType);
                    Assert.True(column.IsNullable);
                    Assert.Equal(128, column.MaxLength);
                }
                checkedTables++;
            }
        }
        Assert.Equal(9, checkedTables);
    }

    [Fact]
    public void LifecycleTables_DoNotAcquireMutableRowAuditColumns()
    {
        var checkedTables = 0;
        foreach (var (context, migration) in InitialMigrations())
        {
            foreach (var table in migration.UpOperations.OfType<CreateTableOperation>()
                .Where(table => !AuditedTables[context].Contains(table.Name, StringComparer.Ordinal)))
            {
                // 创建/发生/接收时间可能是生命周期本身；不为不可变事实伪造最后修改者。
                Assert.DoesNotContain(table.Columns, column => column.Name is "UpdatedAt" or "UpdatedBy");
                checkedTables++;
            }
        }
        Assert.True(checkedTables >= 14, "至少应检查七个上下文的 Inbox 与 Outbox。");
    }

    [Fact]
    public void InitialSchemas_PreserveImportantDatabaseGuarantees()
    {
        var migrations = InitialMigrations();
        var identityChecks = migrations["Identity"].UpOperations.OfType<CreateTableOperation>()
            .SelectMany(table => table.CheckConstraints).Select(check => check.Name).ToArray();
        Assert.Contains("ck_api_resources_http_method", identityChecks);
        Assert.Contains("ck_refresh_tokens_expiry", identityChecks);
        Assert.Contains("ck_users_failed_login_count", identityChecks);
        var settings = Assert.Single(migrations["Platform"].UpOperations.OfType<CreateTableOperation>(), table => table.Name == "global_settings");
        var scope = Column(settings, "Scope");
        Assert.Equal("split_part(\"Key\", '.', 1)", scope.ComputedColumnSql);
        Assert.True(scope.IsStored);
        foreach (var migration in migrations.Values)
        {
            foreach (var table in migration.UpOperations.OfType<CreateTableOperation>())
            {
                Assert.NotNull(table.PrimaryKey);
                Assert.NotEmpty(table.PrimaryKey.Columns);
                Assert.All(table.PrimaryKey.Columns, name =>
                    Assert.Null(Column(table, name).FindAnnotation("Npgsql:ValueGenerationStrategy")));
            }
        }
    }

    private static AddColumnOperation Column(CreateTableOperation table, string name) =>
        Assert.Single(table.Columns, column => column.Name == name);

    private static Dictionary<string, Migration> InitialMigrations()
    {
        var projects = SolutionAssemblies.SourceProjectPaths("*.Infrastructure.csproj", "Services");
        Assert.Equal(7, projects.Count);
        var migrations = new Dictionary<string, Migration>(StringComparer.Ordinal);
        foreach (var path in projects)
        {
            var assembly = SolutionAssemblies.LoadFromTestOutput(Path.GetFileNameWithoutExtension(path) + ".dll");
            // 只枚举公开 Migration 并调用其公开构造函数，不反射私有 DbContext 或存储实现。
            var type = Assert.Single(assembly.GetExportedTypes(), candidate => !candidate.IsAbstract && candidate.IsSubclassOf(typeof(Migration)));
            var context = assembly.GetName().Name!.Split('.')[1];
            Assert.Equal("Initial" + context, type.Name);
            migrations.Add(context, Assert.IsAssignableFrom<Migration>(Activator.CreateInstance(type)));
        }
        Assert.Equal(AuditedTables.Keys.Order(StringComparer.Ordinal), migrations.Keys.Order(StringComparer.Ordinal));
        return migrations;
    }
}
