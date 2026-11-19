using System.Globalization;

namespace HeliVMS.Qctek.Live;

/// <summary>
/// 即時影像第二輸出（G.711 µ-law 到本機 UDP 埠）的參數與節奏。
/// </summary>
/// <remarks>
/// <para>
/// QCC 的即時影像把音訊與影像放在<b>同一條</b>連線上：<c>type=2</c> 的 FRAM 是音軌
/// （codec 200＝G.711 µ-law、payload 1000 bytes）。真機以 8 kHz 送、約 8 包/秒
/// （<see cref="FrameIntervalMilliseconds"/>＝125ms 一包）；客戶端只播放、不做重組，
/// 所以我們也以固定節奏送固定長度的包，而不是「有多少送多少」。
/// </para>
/// <para>
/// <b>音訊跟著影像的 ffmpeg 走，不另開一條 RTSP</b>：便宜攝影機只允許 2～4 條同時連線，
/// 為了聲音再加一條會把畫面擠掉（症狀是「開了聲音就斷畫面」，日誌只看到 RTSP 斷線）。
/// 同一個行程多一個輸出到 <c>udp://127.0.0.1</c>，來不及讀時作業系統丟包——音訊掉幾個
/// 封包只會少一小段聲音，不會反壓回 ffmpeg 把<b>畫面</b>停住（與
/// <c>HeliVMS.Media.AudioOutputArguments</c> 同一個理由）。
/// </para>
/// <para>
/// <b>沒有音軌的來源絕對不能掛 <c>-map 0:a:0</c></b>：ffmpeg 會以「沒有任何輸出串流」
/// 直接結束，連畫面都沒有。多加 <c>?</c>（<c>-map 0:a:0?</c>）救不了——映射允許為空，
/// 但輸出檔仍然一個串流都沒有，結果一樣。所以只有先探測到可轉碼的音軌才掛
/// （<see cref="ShouldAttach"/>），這也是這裡不能只寫一份「支援清單」的原因：
/// 清單錯了有兩種下場，一種是有麥克風卻永遠沒聲音，另一種是整個頻道黑屏。
/// </para>
/// </remarks>
public static class QctekAudioArguments
{
    /// <summary>本機回送位址：只有同一台機器上的接收端拿得到。</summary>
    public const string LoopbackHost = "127.0.0.1";

    /// <summary>輸出取樣率。真機與 QCC 的 G.711 音軌都是 8 kHz。</summary>
    public const int SampleRate = 8000;

    /// <summary>一個 <c>type=2</c> 幀的 payload 長度：8 kHz × 125 ms × 1 byte（µ-law）。</summary>
    public const int FrameLength = 1000;

    /// <summary>每秒送出的音訊幀數（真機實測約 8 包/秒）。</summary>
    public const int FramesPerSecond = 8;

    /// <summary>兩包音訊之間的間隔。</summary>
    public const int FrameIntervalMilliseconds = 1000 / FramesPerSecond;

    /// <summary>
    /// 已排隊但還沒送出的音訊幀上限（4 包＝0.5 秒）。
    /// </summary>
    /// <remarks>
    /// 積壓超過半秒就代表「送不出去」（tick 被拖住、對端太慢），再送舊的音訊只會讓
    /// CMS 的聲音愈來愈落後畫面；丟掉最舊的、永遠送最近的，與影像佇列同一個取捨。
    /// </remarks>
    public const int MaxQueuedFrames = 4;

    /// <summary>ffmpeg 能解碼成 µ-law 的音軌編碼（ffprobe 的 <c>codec_name</c>）。</summary>
    /// <remarks>
    /// 白名單而不是黑名單：清單外的編碼一律當「沒有音訊」。漏判只會少一段聲音
    /// （<c>channels.listen_enabled</c> 是操作員明確打開的，少聲音看得出來），
    /// 誤判則會讓 ffmpeg 在建立輸出時就失敗，整個頻道沒有畫面。
    /// </remarks>
    private static readonly HashSet<string> DecodableCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "pcm_alaw",      // G.711 A-law（VIVOTEK 等常見）
        "pcm_mulaw",     // G.711 µ-law
        "pcm_s16le",
        "pcm_s16be",
        "pcm_s24le",
        "pcm_f32le",
        "aac",
        "mp3",
        "mp2",
        "opus",
        "vorbis",
        "ac3",
        "eac3",
        "g722",
        "adpcm_g726",
    };

    /// <summary>
    /// 這條音軌能不能轉成 µ-law。沒有音軌（<c>null</c>／空字串）或看不懂的編碼回
    /// <c>false</c>——寧可沒有聲音，也不要讓整個頻道黑屏。
    /// </summary>
    public static bool ShouldAttach(string? audioCodec) =>
        !string.IsNullOrWhiteSpace(audioCodec) && DecodableCodecs.Contains(audioCodec);

    /// <summary>
    /// 音訊輸出的 ffmpeg 引數（接在影像輸出 <c>-f h264 -</c> <b>之後</b>，只作用於這一個輸出）。
    /// </summary>
    /// <param name="port">接收端已經綁定的 UDP 埠（必須先綁才能給 ffmpeg）。</param>
    public static IReadOnlyList<string> Build(int port)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);

        return
        [
            "-map", "0:a:0",
            "-ac", "1",
            "-ar", SampleRate.ToString(CultureInfo.InvariantCulture),
            "-acodec", "pcm_mulaw",
            "-f", "mulaw",
            // raw 音訊封裝器預設會把資料積在 AVIO 緩衝（8 kHz 單聲道約可積 1 秒），
            // 現場聲音就慢了一秒；每個封包都沖掉才是即時。
            "-flush_packets", "1",
            $"udp://{LoopbackHost}:{port.ToString(CultureInfo.InvariantCulture)}",
        ];
    }

    /// <summary>這一組引數是不是音訊輸出（給測試與診斷辨識用）。</summary>
    public static bool IsAudioOutputUrl(string argument) =>
        argument.StartsWith($"udp://{LoopbackHost}:", StringComparison.Ordinal);
}
