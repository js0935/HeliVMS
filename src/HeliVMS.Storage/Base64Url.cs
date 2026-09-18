using System.Text;

namespace HeliVMS.Storage;

/// <summary>Base64URL（RFC 4648 §5，無填充）編解碼（M50：JWT／JWKS）。</summary>
public static class Base64Url
{
    public static string Encode(byte[] data)
        => Convert.ToBase64String(data)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    public static string EncodeUtf8(string text) => Encode(Encoding.UTF8.GetBytes(text));

    public static byte[] Decode(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        var remainder = padded.Length % 4;
        padded += remainder switch
        {
            2 => "==",
            3 => "=",
            1 => throw new FormatException("Base64URL 長度無效"),
            _ => string.Empty,
        };

        return Convert.FromBase64String(padded);
    }
}
