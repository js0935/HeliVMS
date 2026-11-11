using System.IO;

namespace HeliVMS.Recording;

/// <summary>
/// 回放前「把正在錄的那一段收檔」的入口。
/// </summary>
/// <remarks>
/// <para>
/// 為什麼需要這個動作：錄影是一段一個檔，正在寫的那一段要等分段時間（預設 60 秒）到期、
/// 收尾、寫進索引之後才會出現在回放清單裡。操作員從即時畫面按「回放」想看的正是
/// 「剛剛發生的事」，而它剛好永遠是清單裡缺的那一段——畫面沒有任何線索說明少了什麼。
/// </para>
/// <para>
/// 桌面端有兩條獨立的錄影路徑：監看牆（監看畫面的 <c>ChannelSession</c>）與排程
/// （<see cref="RecordingScheduler"/>，會跳過已被監看畫面錄影的頻道）。回放前兩邊都要問，
/// 否則同一個按鈕會因為鏡頭剛好在牆上而看到不同長度的錄影。
/// </para>
/// </remarks>
public interface IPlaybackRecordingFinalizer
{
    /// <summary>
    /// 把正在錄的段收檔並等它進索引。
    /// </summary>
    /// <param name="channelId">只收這個頻道；null 表示全部。</param>
    /// <param name="timeout">等待上限；逾時就放棄，回放照常開啟。</param>
    /// <returns>真的收成 final 的段數（0 代表沒有在錄或收檔失敗）。</returns>
    Task<int> FinalizeAsync(int? channelId, TimeSpan timeout);
}

/// <summary>
/// 依序問過每一個錄影來源再回報的實作。
/// </summary>
/// <remarks>
/// 收檔只是「讓最新的一段早點出現」，它失敗不該讓回放開不起來：任何一個來源擲例外都吞掉、
/// 記一行日誌、算 0 段，其餘來源照樣進行。這也是唯一需要日誌的地方——如果沒有這一行，
/// 「按了回放卻看不到最新的一段」在現場完全無法查起。
/// </remarks>
public sealed class PlaybackRecordingFinalizer : IPlaybackRecordingFinalizer
{
    private readonly (string Name, Func<int?, TimeSpan, Task<int>> Source)[] _sources;
    private readonly string _logPath;

    /// <param name="sources">各錄影來源的收檔函式（監看牆、排程），帶上名稱供失敗日誌定位。</param>
    public PlaybackRecordingFinalizer(params (string Name, Func<int?, TimeSpan, Task<int>> Source)[] sources)
        : this(null, sources)
    {
    }

    /// <param name="logPath">失敗日誌路徑；null 表示 <c>%TEMP%\helivms-playback.log</c>。</param>
    /// <param name="sources">各錄影來源的收檔函式（監看牆、排程），帶上名稱供失敗日誌定位。</param>
    public PlaybackRecordingFinalizer(
        string? logPath,
        params (string Name, Func<int?, TimeSpan, Task<int>> Source)[] sources)
    {
        _sources = sources;
        _logPath = logPath ?? Path.Combine(Path.GetTempPath(), "helivms-playback.log");
    }

    public async Task<int> FinalizeAsync(int? channelId, TimeSpan timeout)
    {
        if (_sources.Length == 0)
        {
            return 0;
        }

        var counts = await Task.WhenAll(_sources.Select(source => RunQuietlyAsync(source, channelId, timeout)));
        return counts.Sum();
    }

    private async Task<int> RunQuietlyAsync(
        (string Name, Func<int?, TimeSpan, Task<int>> Source) source,
        int? channelId,
        TimeSpan timeout)
    {
        try
        {
            return await source.Source(channelId, timeout);
        }
        catch (Exception ex)
        {
            // 來源名稱必須進日誌：2026-11 現場的 %TEMP%\helivms-playback.log 有 164 筆
            // 「回放前收檔失敗：TaskCanceledException」（10-30 至 11-12，每次開回放都失敗），
            // 卻完全看不出是監看牆還是排程壞的——只能整條鏈路逐一排查。名稱是定位的第一步。
            HeliVMS.Shared.BoundedFileLog.Append(
                _logPath,
                $"{DateTime.UtcNow:O} 回放前收檔失敗（{source.Name}）：{ex}\n",
                256 * 1024);
            return 0;
        }
    }
}
