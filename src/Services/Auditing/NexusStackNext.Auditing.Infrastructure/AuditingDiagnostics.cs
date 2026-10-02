namespace NexusStackNext.Auditing.Infrastructure;

/// <summary>审计依赖的独立诊断约定，由宿主显式选择健康检查端点。</summary>
public static class AuditingDiagnostics
{
    /// <summary>日志诊断检查标签；业务就绪检查应排除此标签。</summary>
    public const string HealthTag = "auditing-diagnostics";
}
