using System.Globalization;

namespace HeliVMS.Rtc;

/// <summary>M244 即時串流的編碼參數（決定延遲與相容性的取捨）。</summary>
/// <param name="FrameRate">來源影格率；決定 RTP 時脈與 GOP 長度。</param>
/// <param name="BitrateKbps">視訊碼率（kbps）。</param>
/// <param name="GopSeconds">關鍵幀間隔秒數。</param>
public readonly record struct LiveEncodeOptions(int FrameRate, int BitrateKbps, double GopSeconds)
{
    /// <summary>預設值：720p30 / 2 Mbps / 1 秒 GOP。</summary>
    public static LiveEncodeOptions Default { get; } = new(30, 2000, 1.0);

    /// <summary>RTP 動態 payload type；必須與 SDP answer 廣告的一致。</summary>
    public const byte PayloadType = 96;

    /// <summary>
    /// 組出 ffmpeg 參數。
    /// <para>刻意做成純函式：這串參數決定了「低延遲」是否真的成立，屬於可驗證的契約，
    /// 不該埋在 <c>ProcessStartInfo</c> 裡靠人工 review。實測延遲與相容性都取決於它。</para>
    /// </summary>
    public IReadOnlyList<string> BuildArguments(string rtspUrl, string rtpTarget)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rtspUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(rtpTarget);

        // GOP 以影格數表示，並同時鎖住 keyint_min 與關閉 sc_threshold：
        // 沒有這三項，ffmpeg 會因場景切換插入額外關鍵幀，讓「最壞恢復時間」變成未知，
        // 而我們又不能靠瀏覽器的 PLI 救回（見 docs/milestones/M244-webrtc-live.md §7）。
        var gopFrames = Math.Max(1, (int)Math.Round(FrameRate * GopSeconds, MidpointRounding.AwayFromZero));

        return
        [
            "-hide_banner",
            "-loglevel", "error",
            // 與 SegmentRecorder 一致：走 TCP 夾帶，避免 UDP 遺失在真實網路下難以重連。
            "-rtsp_transport", "tcp",
            "-i", rtspUrl,
            // 本里程碑純視訊：攝影機音軌編碼多樣，帶上會讓 Safari 相容性變複雜。
            "-an",
            "-c:v", "libx264",
            "-preset", "veryfast",
            "-tune", "zerolatency",
            // baseline profile 不含 B 影格，這是「不做重排」的前提。
            "-profile:v", "baseline",
            "-pix_fmt", "yuv420p",
            "-b:v", $"{BitrateKbps}k",
            "-maxrate", $"{BitrateKbps}k",
            "-bufsize", $"{BitrateKbps}k",
            "-g", gopFrames.ToString(CultureInfo.InvariantCulture),
            "-keyint_min", gopFrames.ToString(CultureInfo.InvariantCulture),
            "-sc_threshold", "0",
            // rte 輸出 Annex-B 位元流（H.264 over RTP 必要格式）。
            "-f", "rte",
            "-payload_type", PayloadType.ToString(CultureInfo.InvariantCulture),
            rtpTarget,
        ];
    }
}
