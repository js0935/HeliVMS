namespace HeliVMS.Media;

/// <summary>
/// 「這一輪要不要帶 <c>-hwaccel</c>、帶哪一個」的唯一決策點（即時監看與回放共用）。
/// </summary>
/// <remarks>
/// <para>
/// 現場把 <c>decode.hwaccel</c> 設成 <c>auto</c> 的意思是「用這台機器最划算的解碼方式」，
/// 而原本的做法是把字面上的 <c>auto</c> 交給 ffmpeg 自己挑。實測（本機 i5-1035G1、
/// 1080p15、6 路同時、`-re` 即時速率、總 CPU＝各 ffmpeg 的 utime+stime）結果是反的：
/// </para>
/// <list type="bullet">
/// <item>原生 1920×1080：軟解 25.57s、<c>d3d11va</c> 26.17s（打平）、<c>auto</c> 36.42s（最差）。</item>
/// <item>縮到 640×360：軟解 18.72s、<c>d3d11va</c> 8.94s（−52%）、<c>auto</c> 11.09s。</item>
/// <item>縮到 480×270：軟解 24.41s、<c>d3d11va</c> 9.39s（−62%）。</item>
/// <item>16 路 640×360：軟解 61.20s → <c>d3d11va</c> 26.11s（−57%）；24 路 640×360：113.14s → 42.53s。</item>
/// </list>
/// <para>
/// 兩個結論決定了這裡的規則：(1) <b>H.264 的硬體解碼只有在解碼端真的縮尺寸時才划算</b>——
/// 原生尺寸時下載回系統記憶體的成本把省下的解碼成本吃光，甚至更差（見下一段：HEVC 不是這樣）；
/// (2) <b>不能把 <c>auto</c> 直接交給 ffmpeg</b>，它挑的組合在上述每一種情境都比自己挑最差的
/// 還差。所以 <c>auto</c> 由這裡解析成一個明確的方法名（<see cref="AutoPreference"/> 中第一個
/// 本機支援的），且只在「有縮尺寸」或「編碼格式划算」時啟用。
/// </para>
/// <para>
/// 「原生尺寸不值得硬解」<b>只對 H.264 成立</b>，H.265 在原生尺寸就有明顯好處：同一組量測
/// （6 路 1080p15、每路 300 幀、`-re`、總 CPU，兩輪）H.264 8-bit 25.57s vs 26.17s（打平），
/// HEVC 8-bit 40.42/46.86s → 35.47/36.28s（−12%～−23%），HEVC 10-bit 62.73/84.36s →
/// 41.66/46.03s（−34%～−45%）。差別在解碼成本本身：H.264 的軟解很便宜，下載回系統記憶體
/// 的成本就把 GPU 省下的吃光；HEVC（尤其 10-bit）的軟解貴上兩三倍，下載成本只佔一小部分。
/// 所以「這一輪的編碼格式」也是決策輸入，只問縮尺寸會讓 H.265 的機器白白多燒 12–45% CPU，
/// 而畫面完全正常。
/// </para>
/// <para>
/// 明確指定的值（<c>d3d11va</c>／<c>cuda</c>／<c>dxva2</c>）維持既有語意：使用者已經表明要用
/// 那個方法，包含「原生尺寸也要用」，不由這個政策替他決定——只做本機支援與否的檢查。
/// </para>
/// <para>
/// 抽成純函式是為了可測試：這條規則錯了不會有任何錯誤訊息，只會讓 16 路以上的機器默默吃掉
/// 一倍的 CPU，而畫面上看起來完全正常。
/// </para>
/// </remarks>
public static class HardwareDecodePolicy
{
    /// <summary>
    /// <c>auto</c> 的偏好順序（僅列出本機 ffmpeg 實際回報支援者才會被選中）。
    /// </summary>
    /// <remarks>
    /// <c>d3d11va</c> 優先：它是 Windows 上唯一在「縮尺寸解碼」量到大幅節省、且 24 路同時仍
    /// 沒有 GPU session 上限問題的方法。<c>dxva2</c> 是舊 API，只在缺 <c>d3d11va</c> 的環境
    /// 才有意義。<c>qsv</c> 放最後：本機 ffmpeg 有回報它，但實測 6 路全部失敗——它只在
    /// <c>d3d11va</c> 不存在時才會被選到，而那時也只能一試（失敗會進冷卻期退回軟解）。
    /// </remarks>
    public static IReadOnlyList<string> AutoPreference { get; } = ["d3d11va", "dxva2", "qsv"];

    /// <summary>
    /// 解析這一輪實際要用的加速方式；<c>null</c>＝純軟解。
    /// </summary>
    /// <param name="configured">
    /// 設定值（原始字串；內部會經 <see cref="RtspClient.NormalizeHwAccel"/> 正規化）。
    /// <c>null</c>／<c>off</c>／<c>none</c>／<c>false</c>／<c>0</c>＝停用。
    /// </param>
    /// <param name="available">本機 ffmpeg <c>-hwaccels</c> 回報的方法（空集合＝探測失敗）。</param>
    /// <param name="scalingActive">
    /// 這一輪是否真的在解碼端縮小尺寸（見 <see cref="RtspClient.DecodedSize"/>）。
    /// 只有 <c>auto</c> 會參考它：縮尺寸時硬體解碼省一半以上，原生尺寸則要看編碼格式。
    /// </param>
    /// <param name="videoCodec">
    /// 這一輪探測到的編碼格式（<see cref="StreamProbeInfo.VideoCodec"/>，例如 <c>h264</c>／<c>hevc</c>）。
    /// <c>null</c>／空字串＝尚未探測到，此時原生尺寸一律回軟解（猜錯的代價是白吃的 GPU 依賴）。
    /// 只有 <c>auto</c> 會參考它；明確指定的值不受影響。
    /// </param>
    public static string? Resolve(
        string? configured,
        IReadOnlyCollection<string> available,
        bool scalingActive,
        string? videoCodec = null)
    {
        var requested = RtspClient.NormalizeHwAccel(configured);
        if (requested is null)
        {
            return null;
        }

        if (!requested.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            // 明確指定的值：既有語意（清單為空＝探測失敗時仍一試，交由執行期 fallback 兜底）。
            return FfmpegHwAccel.ShouldUse(requested, available) ? requested : null;
        }

        // 原生尺寸時只有「解碼成本本來就貴」的格式值得：H.264 實測打平，H.265 省 12–45%。
        if (!scalingActive && !PaysOffAtNativeSize(videoCodec))
        {
            return null;
        }

        // 探測失敗／無法得知：此時挑任何方法都是猜的，而猜錯的症狀是「連得上卻沒畫面」。
        if (available is null || available.Count == 0)
        {
            return null;
        }

        foreach (var candidate in AutoPreference)
        {
            foreach (var method in available)
            {
                if (method.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 這個編碼格式在<b>原生尺寸</b>下是否仍值得硬體解碼。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 實測（本機 i5-1035G1、6 路 1080p15、每路 300 幀、`-re`、總 CPU，兩輪）：
    /// H.264 8-bit 25.57s vs <c>d3d11va</c> 26.17s（打平，所以維持軟解）；
    /// HEVC 8-bit 40.42/46.86s → 35.47/36.28s（−12%～−23%）、
    /// HEVC 10-bit 62.73/84.36s → 41.66/46.03s（−34%～−45%）。
    /// </para>
    /// <para>
    /// 差別不在「下載回系統記憶體」那一份固定成本（三種格式都要付），而在軟解本身多貴：
    /// H.264 的軟解便宜到抵不過那份成本，HEVC 的軟解貴上兩三倍，於是有賺。
    /// </para>
    /// <para>
    /// 不要改成「列舉 CPU 世代」：第 10 代同時有 Ice Lake 與 Comet Lake（媒體引擎不同代），
    /// 而同一代不同 iGPU 也不同。這裡問的是「這台機器現在這個格式划不划算」，而能回答它的
    /// 只有格式本身與實測；世代名字答不了。
    /// </para>
    /// </remarks>
    public static bool PaysOffAtNativeSize(string? videoCodec)
    {
        if (string.IsNullOrWhiteSpace(videoCodec))
        {
            return false;
        }

        // ffprobe 的 codec_name 是 hevc；mp4 的 codec tag 是 hvc1／hev1；有些舊標頭寫 h265。
        return videoCodec.Trim().Equals("hevc", StringComparison.OrdinalIgnoreCase)
            || videoCodec.Trim().Equals("h265", StringComparison.OrdinalIgnoreCase)
            || videoCodec.Trim().Equals("hvc1", StringComparison.OrdinalIgnoreCase)
            || videoCodec.Trim().Equals("hev1", StringComparison.OrdinalIgnoreCase);
    }
}