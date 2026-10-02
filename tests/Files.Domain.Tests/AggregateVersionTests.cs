using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Domain.Tests;

/// <summary>Files 聚合的版本号（ADR-0011）。</summary>
public sealed class AggregateVersionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MarkStoredAndDelete_Bump_RepeatedDeleteDoesNot()
    {
        var file = StoredFile.Register(
            new StoredFileId(1),
            FileName.Create("a.txt").Value,
            "text/plain",
            ownerId: null,
            Now).Value;

        Assert.Equal(1, file.Version);

        Assert.True(file.MarkStored("key-1", 16).IsSuccess);
        Assert.Equal(2, file.Version);

        Assert.True(file.Delete().IsSuccess);
        Assert.Equal(3, file.Version);

        Assert.True(file.PostponeCleanup(Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(4, file.Version);
        Assert.True(file.PostponeCleanup(Now.AddMinutes(1)).IsSuccess);
        Assert.Equal(4, file.Version);
        Assert.True(file.ConfirmBytesRemoved(Now.AddMinutes(2)).IsSuccess);
        Assert.Equal(5, file.Version);
        Assert.True(file.ConfirmBytesRemoved(Now.AddMinutes(3)).IsSuccess);
        Assert.True(file.PostponeCleanup(Now.AddMinutes(4)).IsSuccess);
        Assert.Equal(5, file.Version);
        Assert.Equal(Now.AddMinutes(2), file.BytesRemovedAt);
        Assert.Null(file.NextCleanupAttemptAt);

        Assert.True(file.Delete().IsSuccess);
        Assert.Equal(5, file.Version);
    }

    /// <summary>
    /// 同一个句柄、同一个大小再标记一次是**空操作**（ADR-0011）——上传重试会走到这里，
    /// 而它不该把一个什么都没变的聚合标成已修改。大小变了才是真的改变。
    /// </summary>
    [Fact]
    public void MarkingStoredAgain_IsANoOpForTheSameKeyAndSize()
    {
        var file = StoredFile.Register(
            new StoredFileId(1),
            FileName.Create("a.txt").Value,
            "text/plain",
            ownerId: null,
            Now).Value;

        Assert.True(file.MarkStored("key-1", 16).IsSuccess);
        var before = file.Version;

        Assert.True(file.MarkStored("key-1", 16).IsSuccess);
        Assert.Equal(before, file.Version);

        Assert.True(file.MarkStored("key-1", 32).IsSuccess);
        Assert.Equal(before + 1, file.Version);
    }
}
