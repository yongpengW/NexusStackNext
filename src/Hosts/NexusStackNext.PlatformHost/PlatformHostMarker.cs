namespace NexusStackNext.PlatformHost;

/// <summary>
/// 平台宿主程序集的**入口标记**——给集成测试用的。
///
/// <para><b>为什么需要它。</b><c>WebApplicationFactory&lt;TEntryPoint&gt;</c> 要一个可引用的类型
/// 来定位宿主程序集。而顶层语句生成的 <c>Program</c> 住在**全局命名空间**里，
/// 网关与平台宿主**各有一个**——一个测试工程同时引用两者会因为 <c>Program</c> 歧义编译不过（CS0433）。</para>
///
/// <para><b>它在网关之后才加，是有意的。</b>票据 52 当时写着"平台宿主的标记类型暂时没加——
/// 目前没有测试需要它，加了就是假设的缝"。现在第一个同时需要两个宿主的测试出现了
/// （Identity 的 HTTP 语义要经平台宿主验证，而网关的 401 要经网关验证），
/// 于是它从"假设的缝"变成了真的缝。</para>
///
/// <para><b>它不承载任何行为</b>——只有一个名字。</para>
/// </summary>
public sealed class PlatformHostMarker;
