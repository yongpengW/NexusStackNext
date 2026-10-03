using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NexusStackNext.IntegrationSupport;

/// <summary>以产品已发布的公开迁移构建旧版测试库，避免复制旧 schema 或访问内部 DbContext。</summary>
internal static class LegacyMigrations
{
    public static async Task ApplyAsync(string connectionString, string schema, params Migration[] migrations)
    {
        if (migrations.Length == 0) { throw new ArgumentException("Legacy migrations must be explicit and nonempty.", nameof(migrations)); }
        var options = new DbContextOptionsBuilder().UseNpgsql(connectionString, builder => builder.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options;
        await using var context = new DbContext(options);
        var generator = context.GetService<IMigrationsSqlGenerator>();
        var history = context.GetService<IHistoryRepository>();
        foreach (var migration in migrations)
        {
            migration.ActiveProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";
            await using var transaction = await context.Database.BeginTransactionAsync().ConfigureAwait(false);
            foreach (var command in generator.Generate(migration.UpOperations, migration.TargetModel))
            {
                if (command.TransactionSuppressed) { throw new InvalidOperationException("Test fixture requires transactional migrations."); }
                await context.Database.ExecuteSqlRawAsync(command.CommandText).ConfigureAwait(false);
            }
            await context.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript()).ConfigureAwait(false);
            var id = migration.GetType().GetCustomAttribute<MigrationAttribute>()?.Id ?? throw new InvalidOperationException("Migration identity is missing.");
            await context.Database.ExecuteSqlRawAsync(history.GetInsertScript(new HistoryRow(id, "10.0.12"))).ConfigureAwait(false);
            await transaction.CommitAsync().ConfigureAwait(false);
        }
    }
}
