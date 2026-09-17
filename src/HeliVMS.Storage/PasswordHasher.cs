using System.Globalization;
using System.Security.Cryptography;

namespace HeliVMS.Storage;

/// <summary>
/// 密碼雜湊（M42，§18.6）：PBKDF2-SHA256（BCL <see cref="Rfc2898DeriveBytes"/>，無第三方套件）。
/// 格式：<c>v1$&lt;iterations&gt;$&lt;saltBase64&gt;$&lt;hashBase64&gt;</c>。
/// </summary>
public static class PasswordHasher
{
    /// <summary>預設迭代次數（OWASP 建議 ≥ 600k；本機應用取 100k 兼顧速度與安全）。</summary>
    public const int DefaultIterations = 100_000;

    private const int SaltSize = 16;
    private const int HashSize = 32;

    /// <summary>產生新密碼雜湊（隨機 salt）。</summary>
    public static string Hash(string password, int iterations = DefaultIterations)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Derive(password, salt, iterations, HashSize);
        return $"v1${iterations.ToString(CultureInfo.InvariantCulture)}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>驗證密碼是否符合已存雜湊；格式不符亦回傳 false。</summary>
    public static bool Verify(string password, string stored)
    {
        ArgumentNullException.ThrowIfNull(password);
        if (string.IsNullOrWhiteSpace(stored))
        {
            return false;
        }

        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != "v1" ||
            !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var iterations) ||
            iterations < 1)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            var actual = Derive(password, salt, iterations, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static byte[] Derive(string password, byte[] salt, int iterations, int outputBytes)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, outputBytes);
}