using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Infrastructure.Persistence;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Infrastructure.Persistence;

/// <summary>Files 独占的元数据模型与迁移历史。</summary>
/// <param name="options">数据库选项。</param>
public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options)
    : NexusStackDbContext(options, SchemaName)
{
    /// <summary>文件上下文所属 schema。</summary>
    public const string SchemaName = "files";

    /// <summary>文件元数据，包括等待清理的软删除记录。</summary>
    public DbSet<StoredFile> Files => Set<StoredFile>();

    internal DbSet<RetiredStorageKey> RetiredStorageKeys => Set<RetiredStorageKey>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        var file = modelBuilder.Entity<StoredFile>();
        file.ToTable("stored_files");
        file.HasKey(item => item.Id);
        file.Property(item => item.Id).HasConversion(id => id.Value, value => new StoredFileId(value));
        file.Property(item => item.Name).HasConversion(name => name.Value, value => FileName.Create(value).Value)
            .HasMaxLength(FileName.MaxLength).IsRequired();
        file.Property(item => item.ContentType).HasMaxLength(255).IsRequired();
        file.Property(item => item.OwnerId).HasMaxLength(128);
        file.Property(item => item.UploadedAt).IsRequired();
        file.Property(item => item.StorageKey).HasMaxLength(128);
        file.HasIndex(item => item.StorageKey).IsUnique();
        file.Property(item => item.Version).IsConcurrencyToken().ValueGeneratedNever();
        file.HasIndex(item => new { item.NextCleanupAttemptAt, item.Id })
            .HasFilter("\"IsDeleted\" AND \"BytesRemovedAt\" IS NULL");
        file.Ignore(item => item.IsStored);
        var retired = modelBuilder.Entity<RetiredStorageKey>();
        retired.ToTable("retired_storage_keys");
        retired.HasKey(item => item.StorageKey);
        retired.Property(item => item.StorageKey).HasMaxLength(128);
    }
}

internal sealed class RetiredStorageKey(string storageKey)
{
    public string StorageKey { get; } = storageKey;
}

internal sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FilesDbContext>()
            .UseNexusStackPostgres("Host=design-time;Database=design-time", FilesDbContext.SchemaName);
        return new FilesDbContext(options.Options);
    }
}
