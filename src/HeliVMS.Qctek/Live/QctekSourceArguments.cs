namespace HeliVMS.Qctek.Live;

/// <summary>
/// 一路影像來源的 ffmpeg 引數（純函式：輸入來源字串，輸出完整的引數清單）。
/// </summary>
/// <remarks>
/// <para>
/// 抽成純函式只為了讓「<c>-re</c> 在不在、在不在 <c>-i</c> 之前」這種順序
/// 有行為測試可釘——即時影像與遠端回放共用同一份引數，兩邊的差別只有「來源是
/// 網路串流還是磁碟上的錄影檔」。實際啟動行程與 stderr 記錄留在 App 端
/// （<c>QctekLiveStreamSource</c>）。
/// </para>
/// <para>
/// <b>檔案來源一定要 <c>-re</c></b>：少了它 ffmpeg 會用磁碟的速度把整段 mp4 灌進
/// stdout，QCC 的回放進度條瞬間跑到尾端、畫面跳格；而且它必須是<b>輸入選項</b>，
/// 也就是排在 <c>-i</c> 之前——排到後面 ffmpeg 會以「cannot be applied to output」
/// 直接結束，症狀變成「回放連得上但沒有畫面」。
/// </para>
/// <para>
/// 網路串流（RTSP／HTTP 等）不加 <c>-re</c>：那些來源本身就是即時速率，
/// 再加一次只會多一層節流。
/// </para>
/// <para>
/// <b>錄影檔的位元流濾鏡一定要帶 <c>h264_mp4toannexb</c>，而且在 <c>dump_extra</c> 之前。</b>
/// mp4／fMP4 的 H.264 是 AVCC（長度前綴）封裝，ffmpeg 對 <c>-f h264</c> 會自動插入
/// <c>h264_mp4toannexb</c> 轉成 Annex-B——<b>但只要操作員自己指定了 <c>-bsf:v</c>，那個自動插入就
/// 不會發生</b>。實測（本機 ffmpeg 8.0.1，mp4 與 fMP4 皆同）：只寫 <c>dump_extra</c> 時
/// ffmpeg 立刻以 <c>Invalid data found when processing input</c> 結束、stdout 零位元組，
/// 症狀與其他「連得上但沒畫面」的故障一模一樣，而 QCC 端與伺服器端日誌都只有這一行。
/// 加上 <c>h264_mp4toannexb</c> 後輸出才以 start code 開頭並帶著 SPS/PPS。
/// 即時影像（RTSP）不能加：那裡的封包本來就是 Annex-B，再轉一次會把 NAL 弄壞。
/// </para>
/// </remarks>
public static class QctekSourceArguments
{
    /// <summary>組出 <c>-f h264</c> 輸出到 stdout 的完整 ffmpeg 引數。</summary>
    /// <param name="source">RTSP／HTTP 位址或錄影檔路徑。</param>
    /// <param name="audioPort">
    /// 已綁定的本機 UDP 埠；大於 0 時在影像輸出<b>之後</b>追加一個 µ-law 音訊輸出
    /// （<see cref="QctekAudioArguments.Build"/>）。0＝只有影像（既有行為）。
    /// </param>
    public static IReadOnlyList<string> Build(string source, int audioPort = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        ArgumentOutOfRangeException.ThrowIfNegative(audioPort);

        var live = IsLiveSource(source);
        var arguments = new List<string>
        {
            "-loglevel",
            "error",
        };

        if (live)
        {
            if (source.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase) ||
                source.StartsWith("rtsps://", StringComparison.OrdinalIgnoreCase))
            {
                // RTSP 專用 demuxer 選項：對檔案來源會「Option not found」（與取影同一規則）。
                arguments.Add("-rtsp_transport");
                arguments.Add("tcp");
            }
        }
        else
        {
            // 檔案來源：依時間戳的速率讀，不要用磁碟速度一次倒完（見型別說明）。
            arguments.Add("-re");
        }

        arguments.Add("-i");
        arguments.Add(source);
        arguments.Add("-an");              // CMS 端只要視訊
        arguments.Add("-c:v");
        arguments.Add("copy");             // 純 remux，不解碼
        arguments.Add("-bsf:v");
        arguments.Add(live
            ? "dump_extra"                    // 關鍵影格前補 SPS/PPS（QCC 以 block1 判關鍵影格）
            : "h264_mp4toannexb,dump_extra"); // 檔案是 AVCC：先轉 Annex-B 再補參數集（見型別說明）
        arguments.Add("-f");
        arguments.Add("h264");
        arguments.Add("-");                // stdout

        if (audioPort > 0)
        {
            // 第二個輸出路徑：`-an` 只屬於前一個輸出（ffmpeg 的選項是「下一個輸出」的），
            // 所以影像仍舊沒有音軌，音訊只從這一個 udp:// 出去。
            foreach (var argument in QctekAudioArguments.Build(audioPort))
            {
                arguments.Add(argument);
            }
        }

        return arguments;
    }

    /// <summary>
    /// 來源是否為「本身即時」的網路串流（不加 <c>-re</c>）。看不懂的來源字串一律
    /// 當檔案處理——多一層節流只會讓它變慢，少一層則會讓回放全速灌幀。
    /// </summary>
    public static bool IsLiveSource(string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);

        foreach (var scheme in LiveSchemes)
        {
            if (source.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>ffmpeg 會自己掌握速率的來源協定。</summary>
    private static readonly string[] LiveSchemes =
    [
        "rtsp://",
        "rtsps://",
        "http://",
        "https://",
        "rtmp://",
        "rtmps://",
        "udp://",
        "tcp://",
        "srt://",
    ];
}
