using System.Text;

namespace HeliVMS.Qctek.Nemo;

/// <summary>
/// NEMO 文字協定（QCTek CMS ↔ NVR 控制通道）的訊息編解碼。
/// </summary>
/// <remarks>
/// <para>
/// 線路格式（由 QCTek QCV8001 的 <c>QC_Server</c> NEMO 派送器與多個客戶端實作靜態反編譯還原）：
/// 固定 <b>16 bytes 標頭</b>＋payload：
/// </para>
/// <code>
/// "NEMO " ＋十進位長度＋空白補滿 16 bytes ＋ payload（payload 位元組數＝該長度）
/// </code>
/// <para>
/// 實例（QCV8001 內建回應字串）：<c>"NEMO 9          OK NV7.1 "</c>——
/// 「NEMO 9」之後補 10 個空白到 16 bytes，而 <c>"OK NV7.1 "</c> 恰好 9 bytes。
/// 伺服端收到請求時會驗證「長度＝訊息位元組數 − 16」，不符即丟棄，
/// 所以標頭與 payload 的長度必須完全一致。
/// </para>
/// <para>
/// 編解碼保持純函式、以位元組為單位（payload 對傳輸層不透明；命令層用 ASCII）。
/// </para>
/// </remarks>
public static class NemoMessage
{
    /// <summary>標頭固定長度（bytes）。</summary>
    public const int HeaderLength = 16;

    private static ReadOnlySpan<byte> Prefix => "NEMO "u8;

    /// <summary>把 payload 包成一個 NEMO 訊框（16 bytes 標頭＋payload）。</summary>
    /// <exception cref="ArgumentOutOfRangeException">payload 過長，標頭放不下十進位長度。</exception>
    public static byte[] Encode(ReadOnlySpan<byte> payload)
    {
        var header = Encoding.ASCII.GetBytes($"NEMO {payload.Length}");
        if (header.Length > HeaderLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(payload), payload.Length, "payload 過長：NEMO 標頭容納不下長度。");
        }

        var buffer = new byte[HeaderLength + payload.Length];
        header.CopyTo(buffer, 0);
        for (var i = header.Length; i < HeaderLength; i++)
        {
            buffer[i] = (byte)' ';
        }

        payload.CopyTo(buffer.AsSpan(HeaderLength));
        return buffer;
    }

    /// <summary>把命令／回應字串包成 NEMO 訊框（UTF-8；命令皆為 ASCII）。</summary>
    public static byte[] EncodeText(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return Encode(Encoding.UTF8.GetBytes(payload));
    }

    /// <summary>
    /// 嘗試從緩衝區開頭解析一個完整 NEMO 訊框。
    /// </summary>
    /// <param name="buffer">累積的位元組流。</param>
    /// <param name="payload">解出的 payload。</param>
    /// <param name="consumed">耗用位元組數（成功時＝16＋payload 長度；資料不足時＝0）。</param>
    /// <returns>訊框完整時 <c>true</c>；資料不足（半個訊框）時 <c>false</c>，呼叫端應繼續讀。</returns>
    /// <exception cref="FormatException">標頭不是 "NEMO " 或缺少長度（串流不同步）。</exception>
    public static bool TryDecode(ReadOnlySpan<byte> buffer, out byte[] payload, out int consumed)
    {
        payload = [];
        consumed = 0;
        if (buffer.Length < HeaderLength)
        {
            return false;
        }

        if (!buffer.StartsWith(Prefix))
        {
            throw new FormatException("不是 NEMO 訊框：標頭未以 'NEMO ' 開頭。");
        }

        var digits = 0;
        var length = 0;
        while (5 + digits < HeaderLength && buffer[5 + digits] is >= (byte)'0' and <= (byte)'9')
        {
            length = (length * 10) + (buffer[5 + digits] - '0');
            digits++;
        }

        if (digits == 0)
        {
            throw new FormatException("NEMO 標頭缺少長度。");
        }

        var total = HeaderLength + length;
        if (buffer.Length < total)
        {
            return false;
        }

        payload = buffer.Slice(HeaderLength, length).ToArray();
        consumed = total;
        return true;
    }

    /// <summary>以文字檢視解析（測試與記錄用；payload 以 UTF-8 解讀）。</summary>
    public static bool TryDecodeText(ReadOnlySpan<byte> buffer, out string payload, out int consumed)
    {
        if (!TryDecode(buffer, out var bytes, out consumed))
        {
            payload = string.Empty;
            return false;
        }

        payload = Encoding.UTF8.GetString(bytes);
        return true;
    }
}
