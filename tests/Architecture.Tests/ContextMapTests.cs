namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// `CONTEXT-MAP.md` 说的话必须是真的。
///
/// <para><b>为什么需要这条检查。</b>第 24 轮 review 拿这份地图逐个名字去代码里找，
/// 11 个名字里**6 个不存在**：`TokenLog`、`Region`、`TaskDue`、`OperationPerformed`、
/// `TokenIssued`、`TokenRevoked`。而那一节整个用**现在时**写着
/// "Identity 发出 `TokenIssued`……"，读的人会以为它已经成立。</para>
///
/// <para>一份读起来像事实、实际是意图的关系图，**比没有关系图更糟**：
/// 下一个人会照着它去 grep 一个不存在的事件，然后怀疑自己的搜索方式。
/// 这跟本仓库反复批判的那些失效是同一类——**声明不受检查，就会在没人注意的时候变成谎话。**</para>
///
/// <para><b>判据。</b>地图里用反引号标出的标识符，要么在 `src/` 或 `tests/` 里真的存在，
/// 要么**同一行**上写明它是计划中的（`计划` / `还没` / `未建` / `不存在` / `预留`）。
/// 两样都不占，就是悬空的。</para>
/// </summary>
public sealed class ContextMapTests
{
    /// <summary>地图里提到的标识符，必须真的存在或有明确的"还没做"标记。</summary>
    [Fact]
    public void ContextMap_NamesOnlyThingsThatExistOrAreMarkedAsPlanned()
    {
        var mapPath = Path.Combine(SolutionAssemblies.RepositoryRoot, "CONTEXT-MAP.md");

        Assert.True(File.Exists(mapPath), $"找不到上下文地图：{mapPath}");

        var map = File.ReadAllText(mapPath);

        // 仓库里所有源码文本（含测试——测试名也是真实存在的标识符）。
        var searchable = new System.Text.StringBuilder();
        foreach (var root in new[] { "src", "tests" })
        {
            var directory = Path.Combine(SolutionAssemblies.RepositoryRoot, root);
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                searchable.AppendLine(File.ReadAllText(file));
            }
        }

        var corpus = searchable.ToString();
        var violations = new List<string>();
        var checkedNames = new List<string>();

        foreach (var line in map.Split('\n'))
        {
            // 同一行上写明了"这是计划中的"，就不必存在。
            var markedAsPlanned = line.Contains("计划", StringComparison.Ordinal)
                || line.Contains("还没", StringComparison.Ordinal)
                || line.Contains("未建", StringComparison.Ordinal)
                || line.Contains("不存在", StringComparison.Ordinal)
                || line.Contains("预留", StringComparison.Ordinal);

            foreach (System.Text.RegularExpressions.Match match in
                System.Text.RegularExpressions.Regex.Matches(line, "`([A-Za-z_][A-Za-z0-9_]*)`"))
            {
                var name = match.Groups[1].Value;
                checkedNames.Add(name);

                // 路径与文件名（`*.Contracts`、`IdentityDomainEvents.cs`）不走这条检查——
                // 它们由别的方式存在，而"文件在不在"有更直接的检查。
                if (line.Contains($"`{name}.", StringComparison.Ordinal) || name.Length < 3)
                {
                    continue;
                }

                if (markedAsPlanned)
                {
                    continue;
                }

                if (!corpus.Contains(name, StringComparison.Ordinal))
                {
                    violations.Add($"`{name}` —— {line.Trim()}");
                }
            }
        }

        // 与其它结构检查同一条规矩：**没有对象可查时不得报告通过**。
        Assert.True(
            checkedNames.Count > 0,
            "一个标识符都没从 CONTEXT-MAP.md 里解析出来——检查等于没跑，不能当作通过。");

        Assert.True(
            violations.Count == 0,
            $"CONTEXT-MAP.md 里提到了源码中不存在、也没有标明是计划中的标识符"
                + $"（解析了 {checkedNames.Count} 个）：\n  "
                + string.Join("\n  ", violations));
    }
}
