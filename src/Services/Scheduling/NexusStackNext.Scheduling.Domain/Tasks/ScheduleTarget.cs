using NexusStackNext.BuildingBlocks.Domain;

namespace NexusStackNext.Scheduling.Domain.Tasks;

/// <summary>计划的固定业务目标；只保存操作种类与对象标识，不保存业务输入。</summary>
public sealed class ScheduleTarget : ValueObject
{
    private ScheduleTarget(string kind, Guid subjectId) => (Kind, SubjectId) = (kind, subjectId);

    /// <summary>执行操作的稳定契约名。</summary>
    public string Kind { get; }
    /// <summary>目标上下文拥有的对象标识。</summary>
    public Guid SubjectId { get; }
    /// <summary>无效或不受支持的目标。</summary>
    public static readonly Error Invalid = new("scheduling.target.invalid", "必须指定受支持的计划目标和非空对象标识。");

    /// <summary>验证目标形状；支持的操作由应用层根据契约限定。</summary>
    /// <param name="kind">目标操作。</param>
    /// <param name="subjectId">业务对象。</param>
    /// <returns>固定目标或校验错误。</returns>
    public static Result<ScheduleTarget> Create(string? kind, Guid subjectId)
    {
        var code = TaskCode.Create(kind);
        return code.IsFailure || subjectId == Guid.Empty
            ? Result.Failure<ScheduleTarget>(Invalid)
            : Result.Success(new ScheduleTarget(code.Value.Value, subjectId));
    }

    /// <inheritdoc />
    protected override IEnumerable<object?> GetEqualityComponents()
    {
        yield return Kind;
        yield return SubjectId;
    }
}
