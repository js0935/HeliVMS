using System.Globalization;

namespace HeliVMS.Rtc;

/// <summary>
/// 從 SDP answer 取出「視訊這條 m-line 真正協商到的 RTP payload type」。
/// </summary>
/// <remarks>
/// <para>
/// 這是「網頁連得上卻永遠黑畫面」的根因之一的修正。我們原本送出的 RTP 固定用
/// <see cref="LiveEncodeOptions.PayloadType"/>（96，我們自己 offer 的編號），但
/// <b>payload type 是對端（瀏覽器）決定的</b>：Chrome 的 offer 用 102／108／116／41 表示各種
/// H.264 profile，SIPSorcery 產生的 answer 就沿用那些編號（本案是 <c>m=video … 102 …</c>）。
/// 於是我們把 H.264 的位元流標成 96 送出去，而 96 在 Chrome 自己的 offer 裡是 <b>VP8</b>：
/// Chrome 把收到的封包交給 VP8 解碼器，一張都解不出來（<c>framesDecoded=0</c>、
/// <c>keyFramesDecoded=0</c>、<c>pliCount</c> 每輪都在要關鍵影格），畫面永遠是黑的，
/// 而日誌與 ICE／DTLS 狀態全部正常——正是最難查的那一種。
/// </para>
/// <para>
/// 抽成純函式是因為它只依賴一段字串，必須能用「真實抓下來的 answer」逐字驗證：
/// 這個 bug 的形狀是「兩個數字不一致」，而兩邊各自看都合理。
/// </para>
/// </remarks>
public static class WhepPayloadType
{
    /// <summary>
    /// 找出 answer 裡視訊 m-line 中屬於 H.264 的 payload type；找不到時回 <c>null</c>
    /// （呼叫端應退回自己的常數，維持舊行為而不是送一個不存在的編號）。
    /// </summary>
    /// <param name="answerSdp">SDP answer（<c>m=video</c> 與 <c>a=rtpmap</c> 都在同一份字串裡）。</param>
    public static int? FromAnswerSdp(string? answerSdp)
    {
        if (string.IsNullOrWhiteSpace(answerSdp))
        {
            return null;
        }

        var lines = answerSdp.Split('\n');
        var payloads = new List<int>();
        var h264 = new HashSet<int>();
        var inVideo = false;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.StartsWith("m=", StringComparison.Ordinal))
            {
                // 只認第一條視訊 m-line：本系統只有一條（只推視訊，不做音訊）。
                inVideo = line.StartsWith("m=video", StringComparison.OrdinalIgnoreCase);
                if (inVideo)
                {
                    // m=<media> <port> <proto> <fmt> …：前三個欄位是埠號與協定，不是 payload type
                    // （把埠號 9 當成編號，送出去的就是一個沒協商過的編號——又是一次黑畫面）。
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    for (var i = 3; i < parts.Length; i++)
                    {
                        if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pt)
                            && pt is > 0 and < 128)
                        {
                            payloads.Add(pt);
                        }
                    }

                    if (payloads.Count == 0)
                    {
                        return null;
                    }
                }

                continue;
            }

            if (!inVideo || !line.StartsWith("a=rtpmap:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // a=rtpmap:102 H264/90000
            var rest = line["a=rtpmap:".Length..].Trim();
            var space = rest.IndexOf(' ');
            if (space <= 0)
            {
                continue;
            }

            if (int.TryParse(rest[..space], NumberStyles.Integer, CultureInfo.InvariantCulture, out var mapped)
                && rest[(space + 1)..].StartsWith("H264", StringComparison.OrdinalIgnoreCase))
            {
                h264.Add(mapped);
            }
        }

        // 依 m-line 的**順序**取第一個屬於 H.264 的編號：那個順序是對端的偏好，
        // 而編號本身的大小沒有意義（Chrome 的 H.264 偏好 102 排在 41 前面，41 才是最小）。
        // answer 的第一個編號不是 H.264 時（對端把別的格式排前面），拿它來送會把 H.264 位元流
        // 標成那個格式——同一個黑畫面，只是換一個方向錯。
        foreach (var payload in payloads)
        {
            if (h264.Contains(payload))
            {
                return payload;
            }
        }

        // 完全沒有視訊 m-line（純音訊 answer、或根本不是 SDP）時回 null，讓呼叫端沿用原本的常數
        // 而不是送一個從別的地方猜來的編號。
        return payloads.Count > 0 ? payloads[0] : null;
    }
}
