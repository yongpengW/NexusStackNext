using System.Reflection;
using NexusStackNext.Files.Domain.Stored;

namespace NexusStackNext.Files.Domain.Tests;

/// <summary>Files：文件名安全，以及存储端口必须保持窄。</summary>
public sealed class FilesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FileName_RejectsPathSeparatorsAndRelativeSegments()
    {
        // 目录穿越的第一道闸在领域里，不在控制器里。
        string[] unsafeNames = ["../etc/passwd", "a/b.pdf", @"c\d.pdf", "..", ".", "a\0b"];

        foreach (var name in unsafeNames)
        {
            var result = FileName.Create(name);

            Assert.True(result.IsFailure, $"应拒绝：{name}");
            Assert.Equal("files.file_name.unsafe", result.Error.Code);
        }
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("  报表 2026.xlsx  ", "报表 2026.xlsx")]
    public void FileName_AcceptsOrdinaryNames(string input, string expected)
    {
        Assert.Equal(expected, FileName.Create(input).Value.Value);
    }

    [Fact]
    public void FileName_RejectsEmptyAndOverlong()
    {
        Assert.True(FileName.Create("   ").IsFailure);
        Assert.True(FileName.Create(new string('a', FileName.MaxLength + 1)).IsFailure);
    }

    [Fact]
    public void Register_RequiresContentType()
    {
        var result = StoredFile.Register(new StoredFileId(1), FileName.Create("a.pdf").Value, "  ", null, Now);

        Assert.True(result.IsFailure);
        Assert.Equal("files.content_type.empty", result.Error.Code);
    }

    [Fact]
    public void MarkStored_RequiresKeyAndNonNegativeSize()
    {
        var file = StoredFile.Register(new StoredFileId(1), FileName.Create("a.pdf").Value, "application/pdf", null, Now).Value;

        Assert.False(file.IsStored);
        Assert.True(file.MarkStored("  ", 10).IsFailure);
        Assert.True(file.MarkStored("k", -1).IsFailure);

        Assert.True(file.MarkStored("storage-key-1", 2048).IsSuccess);
        Assert.True(file.IsStored);
        Assert.Equal(2048, file.Size);
    }

    [Fact]
    public void FileStore_KeepsANarrowSurface()
    {
        // 参照仓库的 IFileStorage 有 14 个成员，且**没有任何一个实现支持全部成员**——
        // AliyunFileStorage.GetAbsolutePath 抛 NotImplementedException，偏偏视频上传路径会调到它。
        // 这条测试是防它重新长回去。
        var methods = typeof(IFileStore).GetMethods();

        Assert.Equal(3, methods.Length);
        Assert.DoesNotContain(methods, static method =>
            method.Name.Contains("Url", StringComparison.Ordinal)
            || method.Name.Contains("Path", StringComparison.Ordinal));
    }

    [Fact]
    public void UrlSigning_IsASeparateCapability_SoLocalStoresNeedNotImplementIt()
    {
        // 做不到的能力不给接口，就没有"声明了却抛异常"的中间状态。
        Assert.NotEqual(typeof(IFileStore), typeof(IFileUrlProvider));
        Assert.Single(typeof(IFileUrlProvider).GetMethods());
        Assert.False(typeof(IFileStore).IsAssignableFrom(typeof(IFileUrlProvider)));
        Assert.False(typeof(IFileUrlProvider).IsAssignableFrom(typeof(IFileStore)));
    }
}
