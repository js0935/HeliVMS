using System.Security.Cryptography;
using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO <c>CLIENT_AUTH</c> 的摘要：<c>MD5(user:middle:pass)</c> 經字組位元組反轉後，
/// 以自訂字母表編成 32 字元。
/// </summary>
/// <remarks>
/// <para>
/// 演算法由 QCTek <c>X86\NetworkAPI.dll</c>（<c>NAPClientCrypCode</c>）與
/// <c>QC_IPCAM_Aver</c>／<c>AVNetworkSDK</c> 的 <c>CLIENT_AUTH_SENDCODE</c> 流程反編譯還原：
/// </para>
/// <list type="number">
/// <item>計算 <c>md5("user:middle:pass")</c>（標準 MD5、單輪、無鹽）；</item>
/// <item>每個 32-bit 字組的位元組反轉（little→big endian）；</item>
/// <item>每個 nibble 以 <c>0→'a'、1→'b' … 5→'f'、6→'0' … 15→'9'</c> 編碼。</item>
/// </list>
/// <para>
/// <c>middle</c>＝連線交涉的交換值（挑戰）。此摘要只做互通用；安全性由雜湊本身承擔，
/// 與 HeliVMS 自己的認證機制無關。
/// </para>
/// </remarks>
public static class QctekAuthDigest
{
    /// <summary>自訂十六進位字母表（0→'a' … 5→'f'、6→'0' … 15→'9'）。</summary>
    public const string Alphabet = "abcdef0123456789";

    /// <summary>計算 32 字元的 <c>CLIENT_AUTH_SENDCODE</c> 值。</summary>
    public static string Compute(string user, string middle, string password)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(middle);
        ArgumentNullException.ThrowIfNull(password);

        var input = Encoding.ASCII.GetBytes($"{user}:{middle}:{password}");
        var digest = MD5.HashData(input);

        var builder = new StringBuilder(32);
        for (var word = 0; word < 4; word++)
        {
            // 字組位元組反轉：little-endian 的 4 bytes 反向輸出。
            for (var b = 3; b >= 0; b--)
            {
                var value = digest[(word * 4) + b];
                builder.Append(Alphabet[value >> 4]);
                builder.Append(Alphabet[value & 0xF]);
            }
        }

        return builder.ToString();
    }
}
