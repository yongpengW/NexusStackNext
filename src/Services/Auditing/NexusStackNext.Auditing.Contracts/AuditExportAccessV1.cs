namespace NexusStackNext.Auditing.Contracts;

/// <summary>审计私有成果的生产者身份与导出许可；下载时使用代码声明，不能由请求指定。</summary>
public static class AuditExportAccessV1
{
    /// <summary>Files 保存的已认证生产者身份。</summary>
    public const string Producer = "auditing";
    /// <summary>提交审计导出所需的操作路由。</summary>
    public const string PermissionRoute = "/api/auditing/exports";
    /// <summary>提交审计导出所需的 HTTP 方法。</summary>
    public const string PermissionMethod = "POST";
}
