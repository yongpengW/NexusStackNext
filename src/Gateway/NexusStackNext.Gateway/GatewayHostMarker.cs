namespace NexusStackNext.Gateway;

/// <summary>
/// 网关程序集的**入口标记**——给集成测试用的。
///
/// <para><b>为什么需要它。</b><c>WebApplicationFactory&lt;TEntryPoint&gt;</c> 要一个可引用的类型
/// 来定位宿主程序集。而顶层语句生成的 <c>Program</c> 住在**全局命名空间**里，
/// 网关与平台宿主**各有一个**——一个测试工程同时引用两者会因为 <c>Program</c> 歧义编译不过（CS0433）。</para>
///
/// <para>给一个**不叫 <c>Program</c>** 的公开标记类型就绕开了这件事。
/// 它与那个 OpenAPI <c>T:Program</c> 冲突是同一类问题的两种表现：
/// 多个程序集各自生成一个全局 <c>Program</c>。</para>
///
/// <para><b>它不承载任何行为</b>——只有一个名字。所以它不会腐坏，
/// 也不会让人以为网关里真有这么一个东西。</para>
/// </summary>
public sealed class GatewayHostMarker;
