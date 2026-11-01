using System.Globalization;

namespace HeliVMS.Media;

/// <summary>
/// 讓既有的解碼 ffmpeg「多吐一份 PCM 到本機 UDP 埠」的參數片段。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼要共用解碼行程而不是另開一條 RTSP 連線：便宜攝影機只允許 2～4 條同時連線
/// （主碼流錄影一條、次碼流監看一條、單分割再看主碼流一條），為了聽聲音再加一條就會把
/// <b>畫面</b>擠掉——症狀是「開了聲音就斷畫面」，而日誌只看到 RTSP 斷線。Frigate 的文件也
/// 是把「便宜的攝影機撐不住多條高解析連線」列為要 restream 的理由（見 restream 章節）。
/// </para>
/// <para>
/// 為什麼是 UDP 而不是 stdout：一個子行程只有一條 stdout，固定大小的 rawvideo 與連續的
/// 位元組流混在一起兩邊都會錯位。給 udp 的好處是「我們來不及讀就丟掉」——音訊掉幾個封包
/// 只會少一小段聲音，若是 TCP，讀取端卡住會反壓回 ffmpeg，連<b>畫面</b>都跟著停。
/// </para>
/// <para>
/// 只有確定量到音訊軌時才會接上這段（見 <see cref="RtspClient"/>）：沒有麥克風的攝影機
/// 什麼都不掛，也就不可能因為音訊而影響畫面。
/// </para>
/// </remarks>
public static class AudioOutputArguments
{
    /// <summary>本機回送位址：只有同一台機器上的接收端拿得到，不會外流。</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>
    /// 決定要不要掛音訊輸出：設定了埠<b>且</b>確定量到音訊軌才掛。
    /// </summary>
    /// <remarks>
    /// 抽成純函式是因為這個決定錯了只有兩種下場，而且都不會出現在畫面上：
    /// 該掛沒掛＝「明明有麥克風卻永遠聽不到」；不該掛亂掛＝對沒有音訊的來源下
    /// <c>-map 0:a:0</c> 會讓 ffmpeg 直接失敗，<b>連畫面都沒有</b>。
    /// </remarks>
    public static int ResolvePort(int configuredPort, bool? sourceHasAudio) =>
        configuredPort > 0 && sourceHasAudio == true ? configuredPort : 0;

    /// <summary>
    /// 音訊輸出的參數（接在影像輸出之後；<c>-map 0:a:0</c> 只作用於這一個輸出路徑）。
    /// </summary>
    /// <param name="port">接收端已經綁定的 UDP 埠（必須先綁才能給 ffmpeg，否則會被系統拒絕）。</param>
    /// <param name="sampleRate">輸出取樣率；必須等於播放裝置協商出的值，否則聲音會忽快忽慢。</param>
    public static IReadOnlyList<string> Build(int port, int sampleRate = AudioStreamReceiver.DefaultSampleRate)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return
        [
            "-map", "0:a:0",
            "-ac", "1",
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture),
            "-acodec", "pcm_s16le",
            "-f", "s16le",
            // rawaudio 封裝器（muxer）預設會把資料積在 AVIO 緩衝（16 kHz 單聲道約可積 1 秒），
            // 現場收音就慢了一秒；每個封包都沖掉才是即時。
            "-flush_packets", "1",
            $"udp://{LoopbackHost}:{port}",
        ];
    }

    /// <summary>這一組參數是不是音訊輸出（給測試與診斷辨識用）。</summary>
    public static bool IsAudioOutputUrl(string argument) =>
        argument.StartsWith($"udp://{LoopbackHost}:", StringComparison.Ordinal);
}
