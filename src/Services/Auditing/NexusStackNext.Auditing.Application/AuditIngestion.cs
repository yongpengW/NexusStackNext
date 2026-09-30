using NexusStackNext.Auditing.Domain.Entries;
using NexusStackNext.BuildingBlocks.Application.Ids;
using NexusStackNext.BuildingBlocks.Application.Time;
using NexusStackNext.BuildingBlocks.Domain;
using NexusStackNext.BuildingBlocks.Application.Events;

namespace NexusStackNext.Auditing.Application;

/// <summary>审计条目存储端口。</summary>
public interface IAuditEntryStore
{
    /// <summary>追加一条。<b>没有修改与删除</b>——审计条目在类型上就不可改写。</summary>
    /// <param name="entry">审计条目。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>任务。</returns>
    Task AddAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}

/// <summary>
/// 待记录的审计事件。
/// <para>
/// <b>它现在住在 Application 里，是被刻意留着的临时位置。</b>当第一个生产者出现
/// （即别的上下文开始发这个事件）时，它应当上移到 <c>Auditing.Contracts</c>——
/// 与架构不变量 7 同一条道理：跨上下文契约要被第二个消费者证明需要才值得独立成程序集。
/// 现在就建一个只有一条消息的 Contracts 程序集，是在为想象中的使用者设计。
/// </para>
/// </summary>
/// <param name="MessageId">消息标识，<b>幂等的依据</b>。</param>
/// <param name="EventName">事件名（含版本）。</param>
/// <param name="Action">动作。</param>
/// <param name="SubjectType">客体类型。</param>
/// <param name="SubjectId">客体标识。</param>
/// <param name="ActorId">操作者。</param>
/// <param name="Detail">细节。</param>
public sealed record AuditIngestedMessage(
    Guid MessageId,
    string EventName,
    string Action,
    string SubjectType,
    string SubjectId,
    string? ActorId,
    string? Detail);

/// <summary>一次摄取的结果。</summary>
public enum IngestionOutcome
{
    /// <summary>首次收到，已记录。</summary>
    Accepted,

    /// <summary>此前已处理过，<b>跳过</b>。</summary>
    Duplicate,
}

/// <summary>
/// 把进入的审计事件变成审计条目。
///
/// <para><b>它是消息基座的第一批真实调用方之一。</b>
/// <see cref="IInboxStore"/> 在此之前只有测试替身——一个适配器只是假设的缝，
/// 而这里它是真的被跨过去了。</para>
///
/// <para><b>幂等键是消息标识，不是业务标识。</b>用"谁做的什么"当键，
/// 两次合法的相同操作会被当成重复而丢掉一次；用消息标识才是在问"这条消息我收过吗"。
/// 参照仓库绑的是业务标识（review/04 F7）。</para>
///
/// <para>接口只有 <see cref="IngestAsync"/> 一个方法，后面是：去重、聚合校验、
/// 两条失败路径、以及一个明确的"跳过"结果。</para>
/// </summary>
/// <param name="inbox">收件箱去重。</param>
/// <param name="entries">审计条目存储。</param>
/// <param name="ids">标识生成器。</param>
/// <param name="clock">时钟。</param>
public sealed class AuditIngestion(
    IInboxStore inbox,
    IAuditEntryStore entries,
    IIdGenerator ids,
    IClock clock)
{
    /// <summary>消费端名称。<b>去重按"消费端 + 事件名 + 消息标识"三者一起判</b>，
    /// 因此同一个事件被两个消费端各自处理一次是正常的，不该互相顶掉。</summary>
    public const string ConsumerName = "auditing.entries";

    /// <summary>摄取一条审计事件。</summary>
    /// <param name="message">事件。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>首次处理返回 <see cref="IngestionOutcome.Accepted"/>；
    /// 重复消息返回 <see cref="IngestionOutcome.Duplicate"/>；条目本身不合法则失败。</returns>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> 为 <c>null</c>。</exception>
    public async Task<Result<IngestionOutcome>> IngestAsync(
        AuditIngestedMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var now = clock.UtcNow;

        // **先把条目建出来，再登记"已处理"。**
        //
        // 顺序不能反：如果先登记再校验，一条非法消息会占掉去重名额——
        // 它被拒了，但再次投递时会被判为 Duplicate，于是**永久静默丢失**，
        // 而且从任何地方都看不出来。这正是 IInboxStore 文档警告的那种失败。
        //
        // 在生产实现里这两步本应在同一个事务里（失败一起回滚），
        // 但把纯校验提到前面，无论有没有事务都是对的。
        var entry = AuditEntry.Record(
            new AuditEntryId(ids.NextId()),
            message.Action,
            message.SubjectType,
            message.SubjectId,
            message.ActorId,
            message.Detail,
            now);

        if (entry.IsFailure)
        {
            return Result.Failure<IngestionOutcome>(entry.Error);
        }

        // 登记与业务改动同生共死（生产实现里靠事务；内存实现只保证同进程内）。
        var isFirstDelivery = await inbox
            .TryBeginProcessingAsync(ConsumerName, message.EventName, message.MessageId, now, cancellationToken)
            .ConfigureAwait(false);

        if (!isFirstDelivery)
        {
            return Result.Success(IngestionOutcome.Duplicate);
        }

        await entries.AddAsync(entry.Value, cancellationToken).ConfigureAwait(false);

        return Result.Success(IngestionOutcome.Accepted);
    }
}
