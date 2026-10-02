namespace NexusStackNext.Costing.Contracts;

/// <summary>Costing 接受的后台委托种类；输入始终由 Costing 自己读取。</summary>
public static class CostingScheduleTarget
{
    /// <summary>重新核算指定对象的当前输入。</summary>
    public const string Recalculate = "costing.recalculate";
}
