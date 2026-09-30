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

        Assert.True(file.Delete().IsSuccess);
        Assert.Equal(3, file.Version);
    }
}
