using NexusStackNext.Identity.Infrastructure;

namespace NexusStackNext.Identity.Application.Tests;

/// <summary>
/// 秘密串的生成与哈希。
///
/// <para><b>第一条测试是这整个设计的支点</b>：刷新令牌的哈希**必须确定性**——
/// 因为刷新时要按哈希把它查回来。而口令哈希（PBKDF2）每次加随机盐，
/// 同样的输入得到不同的输出，用它存刷新令牌的话，**第二次刷新就再也查不到它了**。</para>
/// </summary>
public sealed class SecretHashingTests
{
    /// <summary>同样的输入必须得到同样的输出——这是"按哈希查回来"的前提。</summary>
    [Fact]
    public void Hashing_IsDeterministic()
    {
        var hasher = new Sha256SecretHasher();

        var first = hasher.Hash("some-secret-value");
        var second = hasher.Hash("some-secret-value");

        Assert.Equal(first, second);
    }

    /// <summary>不同的输入得到不同的哈希。</summary>
    [Fact]
    public void DifferentSecrets_ProduceDifferentHashes()
    {
        var hasher = new Sha256SecretHasher();

        Assert.NotEqual(hasher.Hash("secret-a"), hasher.Hash("secret-b"));
    }

    /// <summary>哈希是定长的 64 个十六进制字符（SHA-256），且不含原文。</summary>
    [Fact]
    public void TheHash_IsFixedLength_AndDoesNotContainTheSecret()
    {
        const string Secret = "the-quick-brown-fox";
        var hash = new Sha256SecretHasher().Hash(Secret);

        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(Secret, hash, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>生成的秘密串有足够长度、每次都不同，且是 URL 安全的。</summary>
    [Fact]
    public void GeneratedSecrets_AreLongUnique_AndUrlSafe()
    {
        var generator = new RandomSecretGenerator();

        var secrets = Enumerable.Range(0, 100).Select(_ => generator.Create()).ToList();

        Assert.All(secrets, secret => Assert.True(secret.Length >= 43, $"太短：{secret.Length}"));
        Assert.Equal(secrets.Count, secrets.Distinct(StringComparer.Ordinal).Count());

        // base64url：不含 `+` 与 `/`——它们放进查询串要额外转义。
        Assert.All(secrets, secret =>
        {
            Assert.DoesNotContain('+', secret);
            Assert.DoesNotContain('/', secret);
        });
    }
}
