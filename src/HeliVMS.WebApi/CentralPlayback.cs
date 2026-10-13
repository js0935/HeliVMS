namespace HeliVMS.WebApi;

/// <summary>
/// 跨機回放代理（§14.1 #10 中央/多機集群，M301）：上層 NVR 轉發子節點的 HLS VOD。
/// </summary>
/// <remarks>
/// 子節點播放清單裡的片段網址是相對路徑 <c>/api/stream/segment/{id}/…</c>，指向**子節點自己**。
/// 遠端瀏覽器若直接照著抓，會打到上層 NVR 而不是子節點（檔名一樣、內容不存在），
/// 而且子節點的 Bearer 金鑰不會、也不該出現在瀏覽器端。
/// 因此上層在把清單回給瀏覽器前，先把片段網址改寫成走上層的代理路徑；真正抓片段時
/// 才由伺服器帶子節點金鑰去取。改寫是純字串函式，可離線逐字元驗證。
/// </remarks>
public static class CentralPlayback
{
    /// <summary>代理路由前綴；與 <c>ApiEndpoints</c> 註冊的路由、以及授權閘門規則三者必須一致。</summary>
    public const string RoutePrefix = "/api/central/playback";

    /// <summary>子節點播放清單中片段網址的前綴。</summary>
    private const string ChildSegmentPrefix = "/api/stream/segment/";

    /// <summary>初始化段（EXT-X-MAP）的代理路徑。</summary>
    public static string InitPath(long nodeId, long segmentId) =>
        $"{RoutePrefix}/{nodeId}/segment/{segmentId}/init.mp4";

    /// <summary>媒體段（EXTINF）的代理路徑。</summary>
    public static string MediaPath(long nodeId, long segmentId) =>
        $"{RoutePrefix}/{nodeId}/segment/{segmentId}/media.m4s";

    /// <summary>把子節點播放清單中的片段網址改寫為本代理路徑（保留其餘內容與換行）。</summary>
    public static string Rewrite(string playlist, long nodeId)
    {
        var replacement = $"{RoutePrefix}/{nodeId}/segment/";
        return playlist.Replace(ChildSegmentPrefix, replacement, StringComparison.Ordinal);
    }
}
