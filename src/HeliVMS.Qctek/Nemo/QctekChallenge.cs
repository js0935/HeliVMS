using System.Security.Cryptography;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO <c>QCV8001_AUTH</c> 認證挑戰的產生器。
/// </summary>
/// <remarks>
/// <para>
/// 真機（<c>QC_Server</c> 的 <c>FUN_180008f50</c>）的格式是
/// <b>16 個字元＋一個空白＋十進位整數</b>，字元取自 <c>0123456789+/A-Za-z</c>；
/// 黃金抓包實例：<c>lf6AdoSqOMFtt3ln 1773482693</c>（27 bytes，無結尾 NUL）。
/// </para>
/// <para>
/// <b>尾端整數是十位數，不是「隨便一個整數」。</b>挑戰既沒有結尾 NUL 也沒有長度欄位
/// ——用戶端只能靠「一次 recv 收完」或「讀固定的 27 bytes」。整數少一位就變成 26 bytes，
/// 讀固定長度的用戶端會把下一個回應的第一個位元組當成挑戰的一部分，整條連線從此錯位，
/// 而且兩邊的日誌都正常。黃金值 <c>1773482693</c> 與 Unix 秒相符（2026-03 的某一刻），
/// 因此這裡直接送目前的 Unix 秒：位數與真機一致（十位直到 2286 年），
/// 前綴仍是密碼學亂數，QCC 只把整串當作 digest 的輸入字串。
/// </para>
/// </remarks>
public static class QctekChallenge
{
    /// <summary>真機使用的字元集（順序不影響互通，只影響外觀）。</summary>
    public const string Alphabet = "0123456789+/ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    /// <summary>挑戰字串的隨機前綴長度（真機固定 16）。</summary>
    public const int PrefixLength = 16;

    /// <summary>挑戰字串的固定線路長度（16 ＋ 空白 ＋ 10 位 Unix 秒）。</summary>
    public const int TotalLength = PrefixLength + 1 + 10;

    /// <summary>產生一個 <see cref="TotalLength"/> bytes 的挑戰字串（<c>16 字元＋空白＋十位整數</c>）。</summary>
    public static string Create()
    {
        var chars = new char[PrefixLength];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return $"{new string(chars)} {DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
    }
}
