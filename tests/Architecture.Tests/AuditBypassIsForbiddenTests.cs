namespace NexusStackNext.Architecture.Tests;

/// <summary>
/// **禁止绕过变更跟踪器的写入 API。**
///
/// <para><b>它守的是 ADR-0008。</b>那条决定要求"所有写入路径都必须经过审计拦截器"，
/// 而参照仓库的反例正是一次批量删除绕过了审计——一条本该留痕的删除在审计表里毫无记录。
/// 那种失效方式很安静：**"没留下记录"和"没发生过"看起来一样**。</para>
///
/// <para><b>为什么是源码级守卫，而不是运行期检测。</b>
/// EF Core 的 <c>ExecuteDelete</c> / <c>ExecuteUpdate</c> 直接发 SQL，不经过
/// <c>SaveChanges</c>，因此审计拦截器与领域事件拦截器<b>都看不到它们</b>。
/// 运行期当然可以拦：任何不处于 <c>SavingChanges</c> 窗口内的 <c>DELETE</c>/<c>UPDATE</c>
/// 都算绕过——但那<b>会把手写 SQL 一起误伤</b>（本仓的收件箱就用
/// <c>INSERT … ON CONFLICT</c>），而"误伤"的代价是有人开始给守卫开例外。</para>
///
/// <para>票据 19 的验收原文允许第二种做法："会被审计拦截器发现，<b>或该 API 被禁用</b>"。
/// 这里选的是后者——在**构建期**禁用：它确定性更高，且失败发生在写代码的时候，
/// 而不是运行到那行的时候。</para>
///
/// <para><b>它守不住什么。</b>手写的 <c>ExecuteSqlRaw("DELETE FROM …")</c> 它看不出来——
/// 那需要解析 SQL 文本，而误判的代价同样是"有人开始开例外"。
/// 本仓目前只有一处手写 SQL（收件箱的 <c>INSERT</c>），且它是只增不改的；
/// 真出现手写 DML 时，它应该在评审里被看见，而不是靠一条正则。</para>
/// </summary>
public sealed class AuditBypassIsForbiddenTests
{
    /// <summary>绕过变更跟踪器的 API——用了它们，审计与领域事件拦截器都收不到通知。</summary>
    private static readonly string[] ForbiddenApis =
    [
        ".ExecuteDelete(",
        ".ExecuteDeleteAsync(",
        ".ExecuteUpdate(",
        ".ExecuteUpdateAsync(",
    ];

    /// <summary>产品代码里不得出现这些调用。</summary>
    [Fact]
    public void ProductCode_MustNotUseApisThatBypassTheChangeTracker()
    {
        var sourceRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src");
        Assert.True(Directory.Exists(sourceRoot), $"找不到 {sourceRoot}。");

        var violations = new List<string>();

        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            foreach (var api in ForbiddenApis)
            {
                if (text.Contains(api, StringComparison.Ordinal))
                {
                    violations.Add($"{Path.GetFileName(file)} → {api}");
                }
            }
        }

        Assert.True(
            violations.Count == 0,
            "这些调用绕过了变更跟踪器，因此审计拦截器与领域事件拦截器都看不到它们："
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", violations)
            + Environment.NewLine
            + "请改用跟踪器内的写入（加载实体、改、SaveChanges），或把这条豁免写进评审并说明理由。");
    }

    /// <summary>
    /// 守卫自己也要能失败：确认它扫描的目录里**确实有文件**。
    ///
    /// <para>没有这一条的话，路径写错时上面那条会因为"一个文件都没扫到"而**通过**——
    /// 那正是本仓反复出现的"不会失败的检查"。</para>
    /// </summary>
    [Fact]
    public void TheGuard_ActuallyScansFiles()
    {
        var sourceRoot = Path.Combine(SolutionAssemblies.RepositoryRoot, "src");

        Assert.True(
            Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories).Any(),
            "守卫没有扫到任何 .cs 文件——它守的是空气。");
    }
}
