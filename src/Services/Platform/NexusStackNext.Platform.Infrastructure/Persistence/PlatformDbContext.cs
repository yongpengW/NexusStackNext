using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Platform.Domain.Settings;

namespace NexusStackNext.Platform.Infrastructure.Persistence;

/// <summary>Platform 独占的配置存储模型与迁移历史。</summary>
/// <param name="options">数据库选项。</param>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options)
    : NexusStackDbContext(options, SchemaName)
{
    /// <summary>平台配置所属 schema。</summary>
    public const string SchemaName = "platform";

    /// <summary>全局设置。</summary>
    public DbSet<GlobalSetting> Settings => Set<GlobalSetting>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var setting = modelBuilder.Entity<GlobalSetting>();
        setting.ToTable("global_settings");
        setting.HasKey(item => item.Id);
        setting.Property(item => item.Id).HasConversion(id => id.Value, value => new SettingId(value));
        setting.Property(item => item.Key).HasConversion(key => key.Value, value => SettingKey.Create(value).Value)
            .HasMaxLength(SettingKey.MaxLength).IsRequired();
        setting.HasIndex(item => item.Key).IsUnique().HasDatabaseName("ux_global_settings_key");
        setting.Property<string>("Scope").HasComputedColumnSql("split_part(\"Key\", '.', 1)", stored: true).IsRequired();
        setting.HasIndex("Scope").HasDatabaseName("ix_global_settings_scope");
        setting.Property(item => item.Version).IsConcurrencyToken().ValueGeneratedNever();
    }
}

internal sealed class PlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>()
            .UseNexusStackPostgres("Host=design-time;Database=design-time", PlatformDbContext.SchemaName);
        return new PlatformDbContext(options.Options);
    }
}
