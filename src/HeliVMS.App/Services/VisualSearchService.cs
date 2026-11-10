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
    private readonly Func<string, (byte[] Pixels, int Width, int Height)> _decodeScaled;

    /// <summary>一次補算的列數上限：快照解碼是 CPU 活，分段做、不卡搜尋。</summary>
    public const int BacklogBatchSize = 200;

    /// <summary>搜尋的候選時間窗（天）：更久的歷史去事件中心按時間查。</summary>
    public const int SearchWindowDays = 7;

    /// <summary>搜尋候選上限（與排名門檻共同決定結果集）。</summary>
    public const int CandidateLimit = 2000;

    /// <summary>
    /// 框選搜索的候選上限：框選必須<b>逐張解碼重算</b>（索引存的是整張圖的雜湊），
    /// 所以不能像全圖搜索那樣動輒兩千筆。
    /// </summary>
    /// <remarks>
    /// 400 張在八核上約一到兩秒（每張解到 640 寬）。再放大就會變成「按了框選搜索，
    /// 視窗像當機」；而操作員真正要的是「這幾天裡，框內長這樣的還有幾次」，
    /// 七天內超過 400 筆事件時，先縮小時間範圍或頻道比等更久更合理。
    /// </remarks>
    public const int RoiCandidateLimit = 400;

    /// <param name="events">事件存取（雜湊索引與候選查詢）。</param>
    /// <param name="decodeBgra">
    /// 圖檔 → 索引尺寸（<see cref="VisualHash.IndexWidth"/>×<see cref="VisualHash.IndexHeight"/>）
    /// 的 Bgra32 緩衝。注入是為了繞開 WPF 解碼的測試限制；生產實作是 <see cref="DecodeBgraIndex"/>。
    /// </param>
    /// <param name="decodeScaled">
    /// 圖檔 → 縮到 <see cref="VisualHash.ScaleWidth"/> 寬（較小者維持原尺寸）的 Bgra32 緩衝與實際尺寸。
    /// 框選搜索用：ROI 裁切必須在<b>同樣的解析度</b>下，兩側才對得上同一個畫面區域。
    /// </param>
    public VisualSearchService(
        AlarmEventRepository events,
        Func<string, byte[]>? decodeBgra = null,
        Func<string, (byte[] Pixels, int Width, int Height)>? decodeScaled = null)
    {
        _events = events;
        _decodeBgra = decodeBgra ?? DecodeBgraIndex;
        _decodeScaled = decodeScaled ?? DecodeBgraScaled;
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
    /// <param name="sampleImagePath">樣本圖檔。</param>
    /// <param name="threshold">漢明距離門檻。</param>
    /// <param name="roi">框選區域（null＝整張圖，走索引）。</param>
    public async Task<IReadOnlyList<long>> SearchAsync(
        string sampleImagePath, int threshold, VisualHash.Roi? roi = null)
    {
        if (roi is { } region)
        {
            return await SearchInRegionAsync(sampleImagePath, threshold, region).ConfigureAwait(true);
        }

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
    /// 框選搜索：樣本框內雜湊 vs 候選快照<b>同一個相對框</b>重算的雜湊。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 不能拿索引的 <c>snapshot_hash</c> 來比：那是整張圖的指紋，與框內樣本是兩種東西，
    /// 比出來的距離毫無意義（症狀：框選搜索回的都不像，或什麼都不回）。
    /// </para>
    /// <para>
    /// 候選上限 <see cref="RoiCandidateLimit"/> 是刻意的取捨：每張快照都要解碼一次，
    /// 七天內的事件超過上限時只比對最近的 400 筆——操作員縮小時間或頻道範圍比等更久合理。
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<long>> SearchInRegionAsync(
        string sampleImagePath, int threshold, VisualHash.Roi roi)
    {
        ArgumentNullException.ThrowIfNull(roi);

        var sample = await Task.Run(() => TryHashRegion(sampleImagePath, roi)).ConfigureAwait(true);
        if (sample is not { } sampleHash)
        {
            return [];
        }

        var to = DateTime.UtcNow;
        var from = to.AddDays(-SearchWindowDays);
        var candidates = _events.ListSnapshotCandidates(from, to, RoiCandidateLimit);
        var hashes = await Task.Run(() => HashRegions(candidates, roi)).ConfigureAwait(true);
        var ranked = VisualSearchRank.Rank(sampleHash, hashes, threshold);
        return ranked.Select(m => m.EventId).ToList();
    }

    /// <summary>逐張快照解碼＋框內雜湊（平行，解不開的略過而不是填 0）。</summary>
    private List<(long EventId, ulong Hash)> HashRegions(
        IReadOnlyList<(long Id, string SnapshotPath)> candidates, VisualHash.Roi roi)
    {
        var hashes = new ulong?[candidates.Count];

        // 上限 4 條：快照解碼是記憶體與磁碟密集，全核心下去只會讓 UI 與錄影一起變頓。
        Parallel.For(
            0,
            candidates.Count,
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) },
            i =>
            {
                hashes[i] = TryHashRegion(candidates[i].SnapshotPath, roi);
            });

        var result = new List<(long, ulong)>(candidates.Count);
        for (var i = 0; i < candidates.Count; i++)
        {
            if (hashes[i] is { } h)
            {
                result.Add((candidates[i].Id, h));
            }
        }

        return result;
    }

    /// <summary>單張圖的框內雜湊；解不開回 null（呼叫端略過該候選）。</summary>
    private ulong? TryHashRegion(string imagePath, VisualHash.Roi roi)
    {
        try
        {
            var (pixels, width, height) = _decodeScaled(imagePath);
            return VisualHash.FromBgra(pixels, width, height, roi);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 生產的解碼：WPF 解碼 → 縮到索引尺寸 → Bgra32 CopyPixels。
    /// 灰階與雜湊交給 <see cref="VisualHash"/>（樣本與事件快照同一條管線）。
    /// </summary>
    public static byte[] DecodeBgraIndex(string imagePath)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        return DecodeBgraAt(frame, VisualHash.IndexWidth, VisualHash.IndexHeight).Pixels;
    }

    /// <summary>
    /// 框選搜索的解碼：WPF 解碼 → 縮到 <see cref="VisualHash.ScaleWidth"/> 寬（較小者不放大）
    /// → Bgra32，並回報<b>實際</b>尺寸。
    /// </summary>
    /// <remarks>
    /// 樣本與候選都走這一個方法：框選是相對座標，只有兩側解到同一個尺寸時，
    /// 同一個相對框才會框到同一個畫面區域；一邊 640 寬、一邊 72 寬時，框內的區塊平均
    /// 是兩種不同的取樣，比出來的距離沒有意義。
    /// </remarks>
    public static (byte[] Pixels, int Width, int Height) DecodeBgraScaled(string imagePath)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);

        var (width, height) = VideoScaling.Fit(frame.PixelWidth, frame.PixelHeight, VisualHash.ScaleWidth);
        if (width <= 0 || height <= 0)
        {
            // Fit 回 (0,0) 表示「不需要縮小」：維持原尺寸。
            width = frame.PixelWidth;
            height = frame.PixelHeight;
        }

        return DecodeBgraAt(frame, width, height);
    }

    /// <summary>解碼並轉成 Bgra32；回傳的尺寸是轉換後<b>實際</b>的尺寸（呼叫端必須用它）。</summary>
    private static (byte[] Pixels, int Width, int Height) DecodeBgraAt(
        System.Windows.Media.Imaging.BitmapSource frame, int width, int height)
    {
        var scaled = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(
            width / (double)frame.PixelWidth,
            height / (double)frame.PixelHeight));
        var converter = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);

        // 用轉換後的實際尺寸配置緩衝：TransformedBitmap 的取整與呼叫端算的差一個像素時，
        // CopyPixels 會直接丟例外（或寫出錯位的影格），而那不是「縮小一點」而是整個雜湊錯掉。
        var actualWidth = converter.PixelWidth;
        var actualHeight = converter.PixelHeight;
        var pixels = new byte[actualWidth * actualHeight * 4];
        converter.CopyPixels(pixels, actualWidth * 4, 0);
        return (pixels, actualWidth, actualHeight);
    }
}
