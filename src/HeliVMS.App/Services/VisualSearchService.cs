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
    private readonly Func<string, byte[]> _decodeGray72x64;

    /// <summary>一次補算的列數上限：快照解碼是 CPU 活，分段做、不卡搜尋。</summary>
    public const int BacklogBatchSize = 200;

    /// <summary>搜尋的候選時間窗（天）：更久的歷史去事件中心按時間查。</summary>
    public const int SearchWindowDays = 7;

    /// <summary>搜尋候選上限（與排名門檻共同決定結果集）。</summary>
    public const int CandidateLimit = 2000;

    /// <param name="events">事件存取（雜湊索引與候選查詢）。</param>
    /// <param name="decodeGray72x64">
    /// 圖檔 → 72×64 灰階緩衝（9×8 區塊的取樣網格）。注入是為了繞開 WPF 解碼的測試限制；
    /// 生產實作是 <see cref="DecodeGray72X64"/>。
    /// </param>
    public VisualSearchService(
        AlarmEventRepository events,
        Func<string, byte[]>? decodeGray72x64 = null)
    {
        _events = events;
        _decodeGray72x64 = decodeGray72x64 ?? DecodeGray72X64;
    }

    /// <summary>計算單張圖的感知雜湊；檔案讀不到或解不開回 null（呼叫端略過，不中斷整批）。</summary>
    public ulong? TryHashImage(string imagePath)
    {
        try
        {
            var gray = _decodeGray72x64(imagePath);
            return PerceptualHash.FromGrayImage(gray, PerceptualHash.GrayColumns * 8, PerceptualHash.GrayRows * 8);
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
    /// 生產的灰階解碼：WPF 解碼 → 縮到 72×64 → Bgra32 CopyPixels → 亮度（Rec.709 加權）。
    /// 72×64 讓 <see cref="PerceptualHash.FromGrayImage"/> 的 9×8 區塊每塊 8×8 像素。
    /// </summary>
    public static byte[] DecodeGray72X64(string imagePath)
    {
        var frame = BitmapFrame.Create(
            new Uri(Path.GetFullPath(imagePath)), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var scaled = new TransformedBitmap(frame, new System.Windows.Media.ScaleTransform(
            PerceptualHash.GrayColumns * 8.0 / frame.PixelWidth,
            PerceptualHash.GrayRows * 8.0 / frame.PixelHeight));

        var width = PerceptualHash.GrayColumns * 8;
        var height = PerceptualHash.GrayRows * 8;
        var pixels = new byte[width * height * 4];
        var converter = new FormatConvertedBitmap(scaled, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        converter.CopyPixels(pixels, width * 4, 0);

        var gray = new byte[width * height];
        for (var i = 0; i < gray.Length; i++)
        {
            var b = pixels[i * 4];
            var g = pixels[i * 4 + 1];
            var r = pixels[i * 4 + 2];
            gray[i] = (byte)((r * 299 + g * 587 + b * 114) / 1000);
        }

        return gray;
    }
}
