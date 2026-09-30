using System.Globalization;
using System.Security.Cryptography;
using NexusStackNext.Identity.Application;

namespace NexusStackNext.Identity.Infrastructure;

/// <summary>
/// 基于 PBKDF2-HMAC-SHA256 的口令哈希。
///
/// <para><b>为什么是它。</b>它在 .NET 基础类库里（<see cref="Rfc2898DeriveBytes"/>），
/// 因此不需要引入任何第三方包——而"为了存口令引入一个依赖"本身就是一个供应链决定。
/// 迭代次数取 OWASP 对 PBKDF2-HMAC-SHA256 的建议量级。</para>
///
/// <para><b>编码串自带算法与迭代次数</b>（<c>algo.iterations.salt.hash</c>），
/// 因此将来提高迭代次数时，旧口令仍然校验得动——不需要"让所有人改密码"。</para>
///
/// <para><b>它不是密码学创新。</b>Argon2id 在抗 GPU 上更强，但需要第三方库；
/// 这个实现是"在标准库范围内做出的合理选择"，不是"最优选择"。这条差异写在这里，
/// 免得有人以为这是深思熟虑后的最强方案。</para>
/// </summary>
public sealed class Pbkdf2PasswordHasher : IPasswordHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int Iterations = 210_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    /// <inheritdoc />
    public string Hash(string plainText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);

        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(plainText, salt, Iterations, HashAlgorithmName.SHA256, HashBytes);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Algorithm}.{Iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}");
    }

    /// <inheritdoc />
    public bool Verify(string plainText, string encoded)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(plainText);

        if (string.IsNullOrWhiteSpace(encoded))
        {
            return false;
        }

        var parts = encoded.Split('.');
        if (parts.Length != 4
            || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal)
            || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var iterations)
            || iterations < 1)
        {
            return false;
        }

        byte[] salt;
        byte[] expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(plainText, salt, iterations, HashAlgorithmName.SHA256, expected.Length);

        // 固定时间比较：普通的字节比较会在第一个不同的字节处提前返回，
        // 于是"猜对了几个字节"可以从耗时上看出来。
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
