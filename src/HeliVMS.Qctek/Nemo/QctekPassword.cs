using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO <c>CONNECT</c>／<c>ALARM_REQUEST</c> 的密碼欄位編碼：
/// 標準 Base64（字母表 <c>A–Za–z0–9+/</c>，由 QCTek <c>X86\NVDLL.dll</c> 的
/// 編碼常式與其表逐字還原）。
/// </summary>
/// <remarks>
/// <para>
/// 這只是編碼、不是加密——線路上的密碼等同明文（安全觀察已記錄於分析文件）。
/// 相容層照實作的理由是互通，不是背書。
/// </para>
/// <para>
/// 解碼的判別規則（<see cref="Decode"/>）：<b>能解成合法 UTF-8 的 Base64 才是密碼</b>。
/// CMS 在沒有帳號時送的是字面 <c>guest</c>／<c>test</c>，兩者都自然落回原字串——
/// <c>guest</c> 不是合法 Base64（長度 5）；<c>test</c> 雖是合法 Base64，但解出的位元組
/// <c>B5 EB 2D</c> 不是合法 UTF-8。真密碼的 Base64 一定解得回合法 UTF-8
/// （它本來就是 UTF-8 編出來的），所以這條規則不會誤判真密碼；
/// 判不出來的一律當字面，交由認證去拒絕。
/// </para>
/// </remarks>
public static class QctekPassword
{
    /// <summary>把密碼編成 CMS 期望的 Base64 形式（NVR 端回應或用戶端模擬時使用）。</summary>
    public static string Encode(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(password));
    }

    /// <summary>
    /// 解出 CMS 送來的密碼欄位：合法 Base64 且解回合法 UTF-8 → 密碼原文；
    /// 其餘（字面預設、壞 Base64、非 UTF-8 位元組）→ 原樣回傳，交由認證拒絕。
    /// </summary>
    public static string Decode(string field)
    {
        ArgumentNullException.ThrowIfNull(field);
        Span<byte> buffer = stackalloc byte[(field.Length * 3 / 4) + 3];
        if (Convert.TryFromBase64String(field, buffer, out var written))
        {
            try
            {
                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(buffer[..written]);
            }
            catch (DecoderFallbackException)
            {
                return field; // 合法 Base64 但不是 UTF-8：是字面預設（如 "test"），不是密碼
            }
        }

        return field; // 不是 Base64（如 "guest"）：字面預設
    }
}
