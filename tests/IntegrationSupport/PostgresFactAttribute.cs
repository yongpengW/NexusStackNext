using Xunit;

namespace NexusStackNext.IntegrationSupport;

/// <summary>
/// 需要真实 PostgreSQL 的测试。
///
/// <para><b>为什么要有一个专门的特性，而不是直接 <c>[Fact]</c>。</b>
/// 新克隆的仓库里没有 <c>env/test.dev</c>，而 <b>"跑不起来"不该是新克隆仓库的第一印象</b>——
/// 这与 AgileConfig 那条降级路径是同一条理由（ADR-0006）。</para>
///
/// <para><b>但跳过必须是看得见的。</b>所以它不静默地变成"通过"：
/// xUnit 会把这个用例报成 <c>已跳过</c> 并带上原因，而
/// <c>scripts/run-tests.ps1</c> 在缺配置时**直接失败**——
/// 项目自己的路径上永远不会悄悄跳过一整层测试。</para>
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>创建特性；缺少连接串时把用例标记为跳过。</summary>
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestPostgres.ConnectionStringVariable)))
        {
            Skip = $"需要真实的 PostgreSQL：请设置 {TestPostgres.ConnectionStringVariable}。"
                + "本机开发用 pwsh -File scripts/run-tests.ps1 -Init 生成 env/test.dev。";
        }
    }
}
