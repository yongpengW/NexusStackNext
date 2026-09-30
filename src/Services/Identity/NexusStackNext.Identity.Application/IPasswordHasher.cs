namespace NexusStackNext.Identity.Application;

/// <summary>
/// 口令哈希端口。
/// <para>
/// <b>明文不进领域</b>（见 <c>User.Register</c> 的文档）：端点收到明文，
/// 在这里换成可入库的编码串，领域只认编码串。
/// </para>
/// <para>
/// 它是一个端口而不是一个静态工具类，理由与其它端口一致：换算法时不该改调用方，
/// 而且测试要能塞一个"永远匹配"或"永远不匹配"的替身。
/// </para>
/// </summary>
public interface IPasswordHasher
{
    /// <summary>把明文口令哈希成可入库的编码串。</summary>
    /// <param name="plainText">明文口令。</param>
    /// <returns>编码串（含算法、迭代次数与盐，因此将来换参数也能校验旧口令）。</returns>
    string Hash(string plainText);

    /// <summary>校验明文口令与编码串是否匹配。<b>必须用固定时间比较</b>。</summary>
    /// <param name="plainText">明文口令。</param>
    /// <param name="encoded">此前 <see cref="Hash"/> 产出的编码串。</param>
    /// <returns>匹配则 <c>true</c>。</returns>
    bool Verify(string plainText, string encoded);
}
