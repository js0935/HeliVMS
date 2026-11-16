namespace HeliVMS.Storage;

/// <summary>
/// 決定顯示與錄影各用哪條 RTSP（M76，§15.2）。顯示可切主／次流以省頻寬；
/// 錄影必須固定用主流，否則切到次流時錄影畫質會靜默降低，segment 卻仍標記為 main。
/// 純字串輸入輸出，方便測試，也讓呼叫端不必先解析設備位址。
/// </summary>
public static class StreamRouting
{
    /// <summary>顯示用位址：Sub 且次流位址非空時用次流，否則用主流（缺次流時退回主流，不比現在差）。</summary>
    public static string DisplayUrl(string mainUrl, string subUrl, StreamKind kind)
        => kind == StreamKind.Sub && !string.IsNullOrWhiteSpace(subUrl) ? subUrl : mainUrl;

    /// <summary>錄影用位址：永遠是主流。</summary>
    public static string RecordingUrl(string mainUrl) => mainUrl;

    /// <summary>
    /// 解析用戶端指定的碼流標籤（<c>main</c>／<c>sub</c>），問不出來的一律當主流。
    /// </summary>
    /// <remarks>
    /// 做成寬容而不是 400：這個字串來自 URL，打錯一個字（<c>?stream=subt</c>）若讓請求失敗，
    /// 操作員看到的是一整格空白，而伺服器只回了一個他不會去看的 400；當成主流至少畫得出來。
    /// 大小寫不敏感，因為同一個 <c>stream</c> 參數在 HLS 播放清單上也是大小寫混著被寫入的。
    /// </remarks>
    public static StreamKind ParseLabel(string? label)
        => string.Equals(label?.Trim(), "sub", StringComparison.OrdinalIgnoreCase)
            ? StreamKind.Sub
            : StreamKind.Main;
}
