namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// QCTek 相容層的診斷摘要：把 NEMO 命令整理成可安全寫入日誌的文字。
/// </summary>
/// <remarks>
/// <para>
/// QCC 實測失敗時唯一的線索就是這份日誌——「連不上」「命令不認識」「密碼錯」
/// 在畫面上長得一模一樣，所以每個命令都要留痕（含未知命令：那是 CMS 版本
/// 差異最直接的證據）。
/// </para>
/// <para>
/// <b>但 CONNECT 與 ALARM_REQUEST 的參數帶著 Base64 密碼</b>，而日誌會被複製到
/// 郵件與工單裡——密碼段一律遮蔽：只留使用者名（誰在連）與訂閱號（哪個訂閱），
/// 其餘以 <c>***</c> 代替。這條規則由測試釘住，不是註解裡的承諾。
/// </para>
/// </remarks>
public static class QctekDiagnostics
{
    /// <summary>一般命令摘要的長度上限（避免畸形/超長 payload 灌爆日誌）。</summary>
    public const int MaxSummaryLength = 160;

    /// <summary>把 NEMO 命令整理成可安全寫入日誌的摘要（敏感參數遮蔽、超長截斷）。</summary>
    public static string Summarize(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return "(空)";
        }

        var text = payload.Trim();
        var spaceAt = text.IndexOf(' ');
        var name = spaceAt < 0 ? text : text[..spaceAt];
        var rest = spaceAt < 0 ? string.Empty : text[(spaceAt + 1)..];

        // 敏感命令：只留「誰」與「哪個訂閱」——密碼段絕不落地。
        if (string.Equals(name, "CONNECT", StringComparison.OrdinalIgnoreCase))
        {
            var user = FirstToken(rest);
            return user.Length > 0 ? $"CONNECT {user} ***" : "CONNECT ***";
        }

        if (string.Equals(name, "ALARM_REQUEST", StringComparison.OrdinalIgnoreCase))
        {
            var subscription = FirstToken(rest);
            return subscription.Length > 0 ? $"ALARM_REQUEST {subscription} ***" : "ALARM_REQUEST ***";
        }

        return text.Length <= MaxSummaryLength ? text : text[..MaxSummaryLength] + "…";
    }

    private static string FirstToken(string text)
    {
        var spaceAt = text.IndexOf(' ');
        return spaceAt < 0 ? text : text[..spaceAt];
    }

    /// <summary>
    /// 把「不是 NEMO 框」的原始位元組整理成可讀傾印（前 <paramref name="maxBytes"/> 個）。
    ///
    /// <para>
    /// 這是實測（QCC 加裝置）唯一能回答「對方到底送了什麼」的手段：QCC 對埠送出
    /// 的第一包不是我們預期的命令時（HTTP 探測、別的協定、損壞的串流），沒有這份
    /// 傾印就只能寫「非 NEMO 資料」——等於沒說。可列印 ASCII 原樣顯示（HTTP 請求
    /// 列、產品探測字串一眼可見），其餘以 <c>\xNN</c> 轉義。
    /// </para>
    /// </summary>
    public static string DescribeBytes(ReadOnlySpan<byte> data, int maxBytes = 96)
    {
        if (data.Length == 0)
        {
            return "(0 bytes)";
        }

        var take = Math.Min(data.Length, Math.Max(1, maxBytes));
        var sb = new System.Text.StringBuilder(take + 24);
        for (var i = 0; i < take; i++)
        {
            var b = data[i];
            if (b is >= 0x20 and < 0x7F)
            {
                sb.Append((char)b);
            }
            else if (b == (byte)'\r')
            {
                sb.Append("\\r");
            }
            else if (b == (byte)'\n')
            {
                sb.Append("\\n");
            }
            else
            {
                sb.Append("\\x").Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        if (data.Length > take)
        {
            sb.Append($"…（共 {data.Length} bytes）");
        }

        return sb.ToString();
    }
}
