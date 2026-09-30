using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace NexusStackNext.BuildingBlocks.Infrastructure.Persistence;

/// <summary>
/// 把 <see cref="NexusStackDbContext.Schema"/> 纳入 EF 的模型缓存键。
///
/// <para><b>不这样做会怎样。</b>EF 默认的模型缓存键只有 <c>DbContext</c> 的类型，
/// 而 <c>HasDefaultSchema</c> 的结果是**烘进模型里**的。于是同一个上下文类型
/// 配了不同 schema 时，第二个实例会复用第一个建好的模型——
/// 表建到别处、查询也查别处，而**没有任何东西会报错**，直到某次查询说"表不存在"。</para>
///
/// <para><b>它是被实测逼出来的。</b>票据 19 的集成测试有四个用例，各自用独立的临时 schema。
/// 单独跑全绿，**一起跑就红**（<c>42P01: relation ... does not exist</c>）——
/// 因为只有第一个用例的 schema 真的进了模型。这种"取决于执行顺序"的失败
/// 最难查：它看起来像隔离没做好，实际是缓存键不对。</para>
///
/// <para>缓存键是<b>值相等</b>比较的元组，因此 schema 相同的实例仍共享同一份模型——
/// 生产里每个上下文的 schema 是常量，开销与原来完全一样。</para>
/// </summary>
internal sealed class SchemaAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    /// <inheritdoc />
    public object Create(DbContext context, bool designTime)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context is NexusStackDbContext nexusStack
            ? (context.GetType(), nexusStack.Schema, designTime)
            : (context.GetType(), designTime);
    }
}
