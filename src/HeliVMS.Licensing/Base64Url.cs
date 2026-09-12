using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Licensing;

/// <summary>
/// Base64URL 編碼（RFC 4648 §5，無填充），用於 HELVMS-v2 授權字串。
/// </summary>
internal static class Base64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        if (remainder == 2)
        {
            padded += "==";
        }
        else if (remainder == 3)
        {
            padded += "=";
        }

        return Convert.FromBase64String(padded);
    }
}