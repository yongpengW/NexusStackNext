using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using NexusStackNext.BuildingBlocks.Application.Operations;
using NexusStackNext.BuildingBlocks.Infrastructure.Events;
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
    internal const string DeletionOriginProperty = "DeletionOrigin";

    /// <summary>文件元数据，包括等待清理的软删除记录。</summary>
    public DbSet<StoredFile> Files => Set<StoredFile>();

    internal DbSet<RetiredStorageKey> RetiredStorageKeys => Set<RetiredStorageKey>();

    /// <inheritdoc />
    protected override void ConfigureModel(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ConfigureCommittedFactCleanup();
        modelBuilder.ConfigureCommittedFactCapacity();
        modelBuilder.ConfigureFactCapacityPolicy();
        modelBuilder.ConfigureFileAuditRecovery();
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
        file.Property<ExecutionOrigin?>(DeletionOriginProperty).HasColumnType("jsonb").HasConversion(
            origin => JsonSerializer.Serialize(origin, JsonSerializerOptions.Default),
            json => JsonSerializer.Deserialize<ExecutionOrigin>(json, JsonSerializerOptions.Default));
        file.HasIndex(item => new { item.NextCleanupAttemptAt, item.Id })
            .HasFilter("\"IsDeleted\" AND \"BytesRemovedAt\" IS NULL");
        file.Ignore(item => item.IsStored);
        file.Property<DateTimeOffset?>("CandidateDeadline")
            .HasComputedColumnSql("COALESCE(\"Candidate_ExpiresAt\", \"Candidate_StageExpiresAt\")", stored: true);
        file.HasIndex("CandidateDeadline", nameof(StoredFile.Id))
            .HasFilter("NOT \"IsDeleted\" AND \"Candidate_Producer\" IS NOT NULL");
        file.OwnsOne(item => item.Candidate, candidate =>
        {
            candidate.Property(item => item.Producer).HasMaxLength(32).IsRequired();
            candidate.Property(item => item.OwnerId).HasMaxLength(128).IsRequired();
            candidate.Property(item => item.Sha256).HasMaxLength(64).IsRequired();
            candidate.Property(item => item.Format).HasMaxLength(8).IsRequired();
            candidate.HasIndex(item => new { item.Producer, item.UploadId }).IsUnique();
            candidate.HasIndex(item => new { item.Producer, item.PublicationId }).IsUnique();
        });
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
