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
/// 兩個結論決定了這裡的規則：(1) <b>硬體解碼只有在解碼端真的縮尺寸時才划算</b>——原生尺寸時
/// 下載回系統記憶體的成本把省下的解碼成本吃光，甚至更差；(2) <b>不能把 <c>auto</c> 直接交給
/// ffmpeg</b>，它挑的組合在上述每一種情境都比自己挑最差的還差。所以 <c>auto</c> 由這裡解析成
/// 一個明確的方法名（<see cref="AutoPreference"/> 中第一個本機支援的），且只在有縮尺寸時啟用。
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
    /// 只有 <c>auto</c> 會參考它：原生尺寸時硬體解碼量不到好處，實測甚至更差。
    /// </param>
    public static string? Resolve(
        string? configured,
        IReadOnlyCollection<string> available,
        bool scalingActive)
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

        // 原生尺寸：實測與軟解打平或更差（下載回系統記憶體的成本），不值得多一層 GPU 依賴。
        if (!scalingActive)
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
}
