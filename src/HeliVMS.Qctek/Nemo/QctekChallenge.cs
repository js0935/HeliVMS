using System.Security.Cryptography;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO <c>QCV8001_AUTH</c> 認證挑戰的產生器。
/// </summary>
/// <remarks>
/// <para>
/// 真機（<c>QC_Server</c> 的 <c>FUN_180008f50</c>）的格式是
/// <b>16 個字元＋一個空白＋十進位整數</b>，字元取自
/// <c>0123456789+/A-Za-z</c>，整數來自 <c>rand()</c>；黃金抓包實例：
/// <c>lf6AdoSqOMFtt3ln 1773482693</c>（27 bytes，無結尾 NUL）。
/// </para>
/// <para>
/// 這裡改用 <see cref="RandomNumberGenerator"/> 而不是 <c>rand()</c>：格式必須相同，
/// 但沒有理由讓挑戰值可預測（QCC 只把它當作 digest 的輸入字串，不解析內容）。
/// </para>
/// </remarks>
public static class QctekChallenge
{
    /// <summary>真機使用的字元集（順序不影響互通，只影響外觀）。</summary>
    public const string Alphabet = "0123456789+/ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>挑戰字串的隨機前綴長度（真機固定 16）。</summary>
    public const int PrefixLength = 16;

    /// <summary>產生一個 27 bytes 的挑戰字串（<c>16 字元＋空白＋整數</c>）。</summary>
    public static string Create()
    {
        var chars = new char[PrefixLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        // 真機的整數是正的 int（抓包 1773482693）；避開 0 只是為了看起來像真機。
        var nonce = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        return $"{new string(chars)} {nonce}";
    }
}
