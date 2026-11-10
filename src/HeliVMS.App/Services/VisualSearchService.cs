using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using HeliVMS.Shared;
using HeliVMS.Storage;

namespace HeliVMS.App.Services;

/// <summary>
/// 視覺搜索服務（QC_Live 的 <c>PopupVisualSearch</c> 對齊）：
/// 樣本圖 → 感知雜湊 → 與事件快照雜湊比對 → 命中事件 id 清單（交給事件中心的查詢過濾）。
/// 快照的雜湊索引採<b>懶計算</b>：搜尋前先補算一批（<see cref="BackfillAsync"/>），
/// 不在事件寫入的路徑上加重活。
/// </summary>
public sealed class VisualSearchService
{
    private readonly AlarmEventRepository _events;
    private readonly Func<string, byte[]> _decodeBgra;

    /// <summary>一次補算的列數上限：快照解碼是 CPU 活，分段做、不卡搜尋。</summary>
    public const int BacklogBatchSize = 200;

    /// <summary>搜尋的候選時間窗（天）：更久的歷史去事件中心按時間查。</summary>
    public const int SearchWindowDays = 7;

    /// <summary>搜尋候選上限（與排名門檻共同決定結果集）。</summary>
    public const int CandidateLimit = 2000;

    /// <param name="events">事件存取（雜湊索引與候選查詢）。</param>
    /// <param name="decodeBgra">
    /// 圖檔 → 索引尺寸（<see cref="VisualHash.IndexWidth"/>×<see cref="VisualHash.IndexHeight"/>）
    /// 的 Bgra32 緩衝。注入是為了繞開 WPF 解碼的測試限制；生產實作是 <see cref="DecodeBgraIndex"/>。
    /// </param>
    public VisualSearchService(
        AlarmEventRepository events,
        Func<string, byte[]>? decodeBgra = null)
    {
        _events = events;
        _decodeBgra = decodeBgra ?? DecodeBgraIndex;
    }

    /// <summary>計算單張圖的感知雜湊；檔案讀不到或解不開回 null（呼叫端略過，不中斷整批）。</summary>
    public ulong? TryHashImage(string imagePath)
    {
        try
        {
            // 灰階與區塊取樣一律走共用管線（VisualHash）：樣本與事件快照各寫一份灰階公式，
            // 兩側只要差一點（係數、四捨五入）漢明距離就是亂數，而以圖搜索「看起來能用」。
            var bgra = _decodeBgra(imagePath);
            return VisualHash.FromBgra(bgra, VisualHash.IndexWidth, VisualHash.IndexHeight, roi: null);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>補算索引：最多 <see cref="BacklogBatchSize"/> 筆；回傳本批完成數。</summary>
    public async Task<int> BackfillAsync()
    {
        var backlog = _events.ListSnapshotHashBacklog(BacklogBatchSize);
        var done = 0;
        foreach (var (id, path) in backlog)
        {
            var hash = await Task.Run(() => TryHashImage(path)).ConfigureAwait(true);
            if (hash is { } h)
            {
                _events.SetSnapshotHash(id, h);
                done++;
            }
            else
            {
                // 解不開的快照（損毀檔）標成 0 讓它退出補算清單：0 的雜湊不會命中任何樣本。
                _events.SetSnapshotHash(id, 0);
            }
        }

        return done;
    }

    /// <summary>
    /// 以圖搜尋：樣本雜湊 vs 候選（最近 <see cref="SearchWindowDays"/> 天），
    /// 回命中事件 id（近的在前）；同時確保索引已補算過一批。
    /// </summary>
    public async Task<IReadOnlyList<long>> SearchAsync(string sampleImagePath, int threshold)
    {
        var sample = await Task.Run(() => TryHashImage(sampleImagePath)).ConfigureAwait(true);
        if (sample is not { } sampleHash)
        {
            return [];
        }

        await BackfillAsync().ConfigureAwait(true);

        var to = DateTime.UtcNow;
        var from = to.AddDays(-SearchWindowDays);
        var candidates = _events.ListHashCandidates(from, to, CandidateLimit);
        var ranked = VisualSearchRank.Rank(
            sampleHash,
            candidates.Select(c => (c.Id, c.Hash)),
            threshold);
        return ranked.Select(m => m.EventId).ToList();
    }

    /// <summary>
    /// 生產的解碼：WPF 解碼 → 縮到索引尺寸 → Bgra32 CopyPixels。
    /// 灰階與雜湊交給 <see cref="VisualHash"/>（樣本與事件快照同一條管線）。
    /// </summary>
    public static byte[] DecodeBgraIndex(string imagePath)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var scaled = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(
            VisualHash.IndexWidth / (double)frame.PixelWidth,
            VisualHash.IndexHeight / (double)frame.PixelHeight));

        var width = VisualHash.IndexWidth;
        var height = VisualHash.IndexHeight;
        var pixels = new byte[width * height * 4];
        var converter = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converter.CopyPixels(pixels, width * 4, 0);

        return pixels;
    }
}
