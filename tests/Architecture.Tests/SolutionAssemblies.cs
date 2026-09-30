using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;

namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// 发现本仓产出的程序集，并提供两种读取方式：
/// <list type="bullet">
///   <item>读引用列表用 <see cref="PEReader"/> —— <b>不加载</b>程序集，因此不受依赖解析影响；</item>
///   <item>做类型级检查时才从测试输出目录加载（依赖测试项目已引用全部 src 项目）。</item>
/// </list>
/// </summary>
internal static class SolutionAssemblies
{
    private const string SolutionFileName = "NexusStackNext.slnx";

    /// <summary>仓库根目录（含 NexusStackNext.slnx 的那一层）。</summary>
    public static string RepositoryRoot { get; } = LocateRepositoryRoot();

    /// <summary>当前测试运行配置对应的输出目录片段（<c>\Debug\</c> 或 <c>\Release\</c>）。</summary>
    public static string ConfigurationSegment { get; } =
        AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            ? $"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}"
            : $"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}";

    /// <summary>
    /// 本仓 <c>src/</c> 下已构建的程序集路径。按项目去重（bin 与 obj 下会有重复）。
    /// </summary>
    /// <returns>程序集文件路径，路径序。</returns>
    public static IReadOnlyList<string> BuiltSourceAssemblyPaths() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(RepositoryRoot, "src"), "NexusStackNext.*.dll", SearchOption.AllDirectories)
            .Where(static path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(static path => path.Contains(ConfigurationSegment, StringComparison.Ordinal))
            .Where(static path => !path.Contains($"{Path.DirectorySeparatorChar}ref{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderBy(static path => path, StringComparer.Ordinal)];

    /// <summary>读取程序集直接引用的程序集名（不加载程序集）。</summary>
    /// <param name="assemblyPath">程序集文件路径。</param>
    /// <returns>被引用的程序集简单名。</returns>
    public static IReadOnlyList<string> ReferencedAssemblyNames(string assemblyPath)
    {
        using var stream = File.OpenRead(assemblyPath);
        using var peReader = new PEReader(stream);
        var reader = peReader.GetMetadataReader();

        return
        [
            .. reader.AssemblyReferences
                .Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name))
                .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>从测试输出目录按文件名加载 src 程序集。</summary>
    /// <param name="assemblyFileName">含扩展名的文件名。</param>
    /// <returns>已加载的程序集。</returns>
    /// <exception cref="InvalidOperationException">测试项目没有引用该程序集（文件不在输出目录）。</exception>
    public static Assembly LoadFromTestOutput(string assemblyFileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assemblyFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                $"测试输出目录缺少 {assemblyFileName}。请把对应的 src 项目加进 Architecture.Tests.csproj 的 ProjectReference。");
        }

        return AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    }

    private static string LocateRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"从 {AppContext.BaseDirectory} 向上找不到 {SolutionFileName}，无法定位仓库根目录。");
    }
}
